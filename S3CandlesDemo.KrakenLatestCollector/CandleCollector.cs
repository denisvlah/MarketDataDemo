using S3CandlesDemo.Candles;

namespace S3CandlesDemo.KrakenLatestCollector;

/// <summary>
/// Orchestrates candle collection: reads config, checks S3 for existing data,
/// pages through Kraken API, and stores results to S3.
/// </summary>
public class CandleCollector
{
    private readonly ICandlesRepository _repo;
    private readonly IKrakenOhlcService _krakenService;
    private readonly ILogger<CandleCollector> _logger;
    private readonly TimeSpan _requestDelay;

    public CandleCollector(
        ICandlesRepository repo,
        IKrakenOhlcService krakenService,
        ILogger<CandleCollector> logger,
        TimeSpan? requestDelay = null)
    {
        _repo = repo;
        _krakenService = krakenService;
        _logger = logger;
        _requestDelay = requestDelay ?? TimeSpan.FromSeconds(1.5);
    }

    /// <summary>
    /// Run all collection jobs. Returns true if all succeeded, false if any failed.
    /// </summary>
    public async Task<bool> RunAllAsync(IReadOnlyList<CollectorJobConfig> jobs, DateTime cutoffUtc, CancellationToken ct = default)
    {
        bool allSuccess = true;

        foreach (var job in jobs)
        {
            try
            {
                await RunJobAsync(job, cutoffUtc, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job failed: {AssetPair}/{Interval}", job.AssetPair, job.IntervalMinutes);
                allSuccess = false;
            }
        }

        return allSuccess;
    }

    /// <summary>
    /// Run a single collection job: determine start time, stream all candles from Kraken API, store to S3.
    /// Handles both backfill (start date moved earlier) and forward fill (new candles since last run).
    /// </summary>
    public async Task RunJobAsync(CollectorJobConfig job, DateTime cutoffUtc, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting job: {AssetPair} (Kraken: {KrakenPair}) interval={Interval}min from={StartDate}",
            job.AssetPair, job.KrakenPair, job.IntervalMinutes, job.StartDate);

        var files = await _repo.GetCandleFilesAsync(job.AssetPair, job.IntervalMinutes, ct);

        if (files.Count == 0)
        {
            // No existing data — collect from start date to cutoff
            _logger.LogInformation("No existing data for {AssetPair}/{Interval}. Collecting from {Since}",
                job.AssetPair, job.IntervalMinutes, job.StartDate);

            await CollectRangeAsync(job, job.StartDate, cutoffUtc, ct);
        }
        else
        {
            var earliestStart = files.Min(f => f.Start);
            var latestEnd = files.Max(f => f.End);

            // Backfill: if start date in config is earlier than earliest stored candle
            if (job.StartDate < earliestStart)
            {
                _logger.LogInformation("Backfilling {AssetPair}/{Interval} from {From} to {To}",
                    job.AssetPair, job.IntervalMinutes, job.StartDate, earliestStart);

                await CollectRangeAsync(job, job.StartDate, earliestStart, ct);
            }

            // Forward fill: collect from after the latest stored candle to cutoff
            var forwardFrom = latestEnd.AddMinutes(job.IntervalMinutes);
            if (forwardFrom < cutoffUtc)
            {
                _logger.LogInformation("Forward filling {AssetPair}/{Interval} from {From}",
                    job.AssetPair, job.IntervalMinutes, forwardFrom);

                await CollectRangeAsync(job, forwardFrom, cutoffUtc, ct);
            }
            else
            {
                _logger.LogInformation("Already up to date for {AssetPair}/{Interval}. Nothing to do.",
                    job.AssetPair, job.IntervalMinutes);
            }
        }

        _logger.LogInformation("Completed job: {AssetPair}/{Interval}", job.AssetPair, job.IntervalMinutes);
    }

    /// <summary>
    /// Collect candles for a specific time range and store them to S3.
    /// </summary>
    private async Task CollectRangeAsync(CollectorJobConfig job, DateTime from, DateTime to, CancellationToken ct)
    {
        if (from >= to) return;

        var candleStream = FetchAllCandlesAsync(job, from, to, ct);
        await _repo.StoreCandlesAsync(job.AssetPair, job.IntervalMinutes, candleStream, ct);
    }

    /// <summary>
    /// Streams all candles from the Kraken API across multiple paginated batches as a single IAsyncEnumerable.
    /// </summary>
    private async IAsyncEnumerable<Candle> FetchAllCandlesAsync(
        CollectorJobConfig job, DateTime since, DateTime cutoffUtc,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        int totalCandles = 0;

        while (since < cutoffUtc)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await _krakenService.GetOhlcAsync(job.KrakenPair, job.IntervalMinutes, since, ct);

            if (batch.Candles.Count == 0)
            {
                _logger.LogInformation("No more candles for {AssetPair}/{Interval}. Done.", job.AssetPair, job.IntervalMinutes);
                yield break;
            }

            bool reachedCutoff = false;
            foreach (var candle in batch.Candles)
            {
                if (candle.Timestamp >= cutoffUtc)
                {
                    reachedCutoff = true;
                    break;
                }

                yield return candle;
                totalCandles++;
                since = candle.Timestamp.AddMinutes(job.IntervalMinutes);
            }

            if (reachedCutoff)
            {
                _logger.LogInformation("Reached cutoff for {AssetPair}/{Interval}. Done.", job.AssetPair, job.IntervalMinutes);
                yield break;
            }

            _logger.LogInformation("Streamed {Total} candles so far for {AssetPair}/{Interval}, continuing...",
                totalCandles, job.AssetPair, job.IntervalMinutes);

            // Rate limiting delay between API requests
            if (_requestDelay > TimeSpan.Zero)
                await Task.Delay(_requestDelay, ct);
        }
    }

}

using Amazon.S3;
using S3CandlesDemo.Candles;

namespace S3CandlesDemo.CsvLoader;

/// <summary>
/// Background service that detects gaps in the candles repository and fills them from CSV files in S3.
/// Runs automatically on startup and exits the application when complete.
/// </summary>
public class GapFillingService : BackgroundService
{
    private readonly ICandlesRepository _repo;
    private readonly IAmazonS3 _s3Client;
    private readonly ICsvSource _csvSource;
    private readonly IConfiguration _config;
    private readonly ILogger<GapFillingService> _logger;
    private readonly GapFillingHealthCheck _healthCheck;
    private readonly IHostApplicationLifetime _lifetime;

    public GapFillingService(
        ICandlesRepository repo,
        IAmazonS3 s3Client,
        ICsvSource csvSource,
        IConfiguration config,
        ILogger<GapFillingService> logger,
        GapFillingHealthCheck healthCheck,
        IHostApplicationLifetime lifetime)
    {
        _repo = repo;
        _s3Client = s3Client;
        _csvSource = csvSource;
        _config = config;
        _logger = logger;
        _healthCheck = healthCheck;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int exitCode = 0;
        try
        {
            // 1. Read config from S3
            var s3Config = _config.GetSection("S3Candles");
            var configBucket = s3Config.GetValue<string>("ConfigBucket") ?? "candles-config";
            var configKey = s3Config.GetValue<string>("ConfigKey") ?? "kraken-collector-config.csv";

            _logger.LogInformation("Reading config from S3: {Bucket}/{Key}", configBucket, configKey);
            List<LoaderJobConfig> jobs;
            try
            {
                jobs = await ConfigReader.ReadFromS3Async(_s3Client, configBucket, configKey, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to load config from S3 ({Bucket}/{Key})", configBucket, configKey);
                Environment.Exit(1);
                return;
            }
            _logger.LogInformation("Loaded {Count} config entries", jobs.Count);

            // 2. Build repository file index
            await _repo.RebuildFileIndexAsync(stoppingToken);

            // 3. List all CSV file names from the CSV source (no downloads)
            var csvFiles = await _csvSource.ListFilesAsync(stoppingToken);

            // 4. Determine worker count
            var workers = 1;
            var workersEnv = Environment.GetEnvironmentVariable("WORKERS");
            if (!string.IsNullOrEmpty(workersEnv) && int.TryParse(workersEnv, out var w) && w > 0)
                workers = w;
            _logger.LogInformation("Gap detection started for {Count} symbol/interval pairs with {Workers} workers",
                jobs.Count, workers);

            // 5. Process each (symbol, interval) pair with throttled parallelism
            using var semaphore = new SemaphoreSlim(workers);
            bool anyError = false;
            var tasks = jobs.Select(job => Task.Run(async () =>
            {
                await semaphore.WaitAsync(stoppingToken);
                try
                {
                    await ProcessJobAsync(job, csvFiles, stoppingToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing {Symbol} ({Interval}min)", job.AssetPair, job.IntervalMinutes);
                    anyError = true;
                }
                finally
                {
                    semaphore.Release();
                }
            }, stoppingToken)).ToArray();

            await Task.WhenAll(tasks);

            if (anyError)
            {
                _logger.LogError("Some gap-filling operations failed");
                exitCode = 2;
            }
            else
            {
                _logger.LogInformation("All gap-filling operations completed");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Gap-filling cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error during gap-filling");
            exitCode = 2;
        }
        finally
        {
            Environment.ExitCode = exitCode;
            _lifetime.StopApplication();
        }
    }

    private async Task ProcessJobAsync(
        LoaderJobConfig job, List<CsvFileInfo> allCsvFiles, CancellationToken ct)
    {
        var symbol = job.AssetPair;
        var interval = job.IntervalMinutes;
        var minDate = job.StartDate;

        // Normalize symbol names for CSV matching: strip slashes
        var normalizedAssetPair = symbol.Replace("/", "");
        var normalizedKrakenPair = job.KrakenPair.Replace("/", "");

        // Get gaps from the repository
        var gaps = _repo.GetGaps(symbol, interval, minDate);
        if (gaps.Count == 0)
        {
            _logger.LogInformation("{Symbol} ({Interval}min): No gaps detected", symbol, interval);
            return;
        }

        _logger.LogInformation("{Symbol} ({Interval}min): Detected {Count} gaps", symbol, interval, gaps.Count);

        // Find CSV files matching this symbol/interval using both asset pair and kraken pair names
        var matchingCsvFiles = allCsvFiles
            .Where(f => f.IntervalMinutes == interval &&
                        (string.Equals(f.Symbol, normalizedAssetPair, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(f.Symbol, normalizedKrakenPair, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f.Start)
            .ToList();

        if (matchingCsvFiles.Count == 0)
        {
            _logger.LogWarning("{Symbol} ({Interval}min): No CSV files found matching '{NormalizedAsset}' or '{NormalizedKraken}'",
                symbol, interval, normalizedAssetPair, normalizedKrakenPair);
            return;
        }

        // For each gap, find CSV files that overlap and stream candles
        foreach (var gap in gaps)
        {
            var overlapping = matchingCsvFiles
                .Where(f => f.End >= gap.Start && f.Start <= gap.End)
                .ToList();

            if (overlapping.Count == 0)
            {
                _logger.LogDebug("{Symbol} ({Interval}min): No CSV coverage for gap {Start} - {End}",
                    symbol, interval, gap.Start, gap.End);
                continue;
            }

            foreach (var csvFile in overlapping)
            {
                await FillGapFromCsvAsync(symbol, interval, gap, csvFile, ct);
            }
        }
    }

    private async Task FillGapFromCsvAsync(
        string symbol, int interval, (DateTime Start, DateTime End) gap,
        CsvFileInfo csvFile, CancellationToken ct)
    {
        var gapStart = gap.Start;
        var gapEnd = gap.End;

        _logger.LogInformation("{Symbol} ({Interval}min): Loading from {CsvKey} for gap {Start} - {End}",
            symbol, interval, Path.GetFileName(csvFile.Key), gapStart,
            gapEnd == DateTime.MaxValue ? "open" : gapEnd.ToString());

        // Open the CSV stream with exponential backoff retry (3 attempts)
        Stream? stream = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                stream = await _csvSource.OpenReadStreamAsync(csvFile.Key, ct);
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < 3)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(ex, "Attempt {Attempt}/3 failed to open {Key}, retrying in {Delay}s",
                    attempt, csvFile.Key, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }

        if (stream == null)
        {
            _logger.LogError("Failed to open CSV file {Key} after 3 attempts", csvFile.Key);
            throw new IOException($"Failed to open CSV file {csvFile.Key} after 3 attempts");
        }

        // TODO: Implement YieldCandlesForGap — enumerator reuse and gap-boundary yielding logic.
        // This stub filters candles from the CSV stream to only those within the gap boundaries,
        // and feeds them into StoreCandlesAsync. The full implementation should reuse enumerators
        // across consecutive gaps within the same CSV file.
        // Signature: IAsyncEnumerable<Candle> YieldCandlesForGap(IAsyncEnumerable<Candle> source, DateTime gapStart, DateTime gapEnd)

        await using (stream)
        {
            var gapCandles = FilterCandlesForGap(CsvCandleReader.ReadCandlesAsync(stream, ct), gapStart, gapEnd);
            await _repo.StoreCandlesAsync(symbol, interval, gapCandles, ct);
            _healthCheck.ReportProgress();
        }
    }

    /// <summary>
    /// Filters candles from the source stream to only yield those within [gapStart, gapEnd).
    /// Skips records before gapStart and stops after gapEnd.
    /// NOTE: This is a simplified implementation. The full enumerator-reuse across gaps
    /// should be implemented in YieldCandlesForGap (see TODO above).
    /// </summary>
    private static async IAsyncEnumerable<Candle> FilterCandlesForGap(
        IAsyncEnumerable<Candle> source, DateTime gapStart, DateTime gapEnd,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var candle in source.WithCancellation(ct))
        {
            if (candle.Timestamp < gapStart) continue;
            if (candle.Timestamp >= gapEnd) yield break;
            yield return candle;
        }
    }
}

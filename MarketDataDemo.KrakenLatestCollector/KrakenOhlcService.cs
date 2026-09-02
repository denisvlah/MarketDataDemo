using Kraken.Net.Enums;
using Kraken.Net.Interfaces.Clients;
using MarketDataDemo.Candles;

namespace MarketDataDemo.KrakenLatestCollector;

/// <summary>
/// Wraps the KrakenExchange.Net SDK to fetch OHLC candle data.
/// Rate limiting and rate-limit retries are handled by the SDK (CryptoExchange.Net).
/// This service adds retries for transient network/server errors only.
/// </summary>
public class KrakenOhlcService : IKrakenOhlcService
{
    private readonly IKrakenRestClient _client;
    private readonly ILogger<KrakenOhlcService> _logger;
    private const int MaxRetries = 3;

    private static readonly Dictionary<int, KlineInterval> IntervalMap = new()
    {
        { 1, KlineInterval.OneMinute },
        { 5, KlineInterval.FiveMinutes },
        { 15, KlineInterval.FifteenMinutes },
        { 30, KlineInterval.ThirtyMinutes },
        { 60, KlineInterval.OneHour },
        { 240, KlineInterval.FourHour },
        { 1440, KlineInterval.OneDay },
        { 10080, KlineInterval.OneWeek },
        { 21600, KlineInterval.FifteenDays },
    };

    public KrakenOhlcService(IKrakenRestClient client, ILogger<KrakenOhlcService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<KrakenOhlcBatch> GetOhlcAsync(string krakenPair, int intervalMinutes, DateTime since, CancellationToken ct = default)
    {
        if (!IntervalMap.TryGetValue(intervalMinutes, out var klineInterval))
            throw new ArgumentException($"Unsupported interval: {intervalMinutes}");

        Exception? lastEx = null;
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var result = await _client.SpotApi.ExchangeData.GetKlinesAsync(
                krakenPair, klineInterval, since: since, ct: ct);

            if (result.Success && result.Data != null)
            {
                var allKlines = result.Data.Data.ToList();

                // Discard the last entry — it's the current uncommitted candle
                if (allKlines.Count > 1)
                    allKlines.RemoveAt(allKlines.Count - 1);
                else if (allKlines.Count <= 1)
                    return new KrakenOhlcBatch(new List<Candle>(), null);

                var candles = allKlines.Select(k => new Candle
                {
                    Timestamp = k.OpenTime,
                    Open = (double)k.OpenPrice,
                    High = (double)k.HighPrice,
                    Low = (double)k.LowPrice,
                    Close = (double)k.ClosePrice,
                    Volume = (double)k.Volume,
                    TradeCount = k.TradeCount
                }).ToList();

                DateTime? lastTimestamp = result.Data.LastUpdateTime;

                return new KrakenOhlcBatch(candles, lastTimestamp);
            }

            lastEx = result.Error != null ? new Exception(result.Error.ToString()) : new Exception("Unknown Kraken API error");
            _logger.LogWarning("Kraken API attempt {Attempt}/{MaxRetries} failed for {Pair}/{Interval}: {Error}",
                attempt, MaxRetries, krakenPair, intervalMinutes, result.Error);

            if (attempt < MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay, ct);
            }
        }

        throw new Exception($"Kraken API failed after {MaxRetries} retries for {krakenPair}/{intervalMinutes}", lastEx);
    }
}

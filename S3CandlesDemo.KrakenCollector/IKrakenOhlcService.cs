using S3CandlesDemo.Candles;

namespace S3CandlesDemo.KrakenCollector;

/// <summary>
/// Represents a batch of candles fetched from the Kraken API.
/// </summary>
public record KrakenOhlcBatch(
    List<Candle> Candles,
    DateTime? LastTimestamp   // The "last" value from Kraken, used as the next "since" for pagination
);

/// <summary>
/// Abstraction over the Kraken OHLC API for testability.
/// </summary>
public interface IKrakenOhlcService
{
    /// <summary>
    /// Fetch OHLC candles for a given pair and interval starting from <paramref name="since"/>.
    /// Returns up to 720 candles per call. The last entry (uncommitted) is discarded.
    /// </summary>
    Task<KrakenOhlcBatch> GetOhlcAsync(string krakenPair, int intervalMinutes, DateTime since, CancellationToken ct = default);
}

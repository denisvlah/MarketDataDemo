namespace S3CandlesDemo.KrakenLatestCollector;

/// <summary>
/// Represents a single row from kraken-collector-config.csv.
/// </summary>
public record CollectorJobConfig(
    string AssetPair,      // Canonical name for S3 storage (e.g. "BTCUSD")
    string KrakenPair,     // Pair name sent to Kraken API (e.g. "XBTUSD")
    int IntervalMinutes,   // Candle interval in minutes
    DateTime StartDate     // Earliest date to collect from
);

namespace S3CandlesDemo.CsvLoader;

/// <summary>
/// Represents a single row from the unified kraken-collector-config.csv.
/// </summary>
public record LoaderJobConfig(
    string AssetPair,      // Canonical name for S3 storage (e.g. "BTCUSD")
    string KrakenPair,     // Pair name used by Kraken (e.g. "XBTUSD")
    int IntervalMinutes,   // Candle interval in minutes
    DateTime StartDate     // Earliest date to collect from (used as minDate for gap detection)
);

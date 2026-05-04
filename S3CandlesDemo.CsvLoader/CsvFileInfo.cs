namespace S3CandlesDemo.CsvLoader;

/// <summary>
/// Represents a CSV file's metadata extracted from its S3 key name.
/// File naming convention: {Symbol}_{IntervalMinutes}_{Start}_{End}.csv
/// </summary>
public record CsvFileInfo(
    string Key,             // Full S3 object key
    string Symbol,          // Symbol without slashes (e.g. "ETHEUR")
    int IntervalMinutes,    // Candle interval in minutes
    DateTime Start,         // First candle timestamp from filename
    DateTime End            // Last candle timestamp from filename
);

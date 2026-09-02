namespace MarketDataDemo.CsvLoader;

/// <summary>
/// Abstraction over the CSV data source (S3, local filesystem, etc.).
/// Enables testing CSV loading without a real S3 connection.
/// </summary>
public interface ICsvSource
{
    /// <summary>Lists all CSV files available in this source.</summary>
    Task<List<CsvFileInfo>> ListFilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens a read stream for the CSV file identified by <paramref name="key"/>.
    /// The caller is responsible for disposing the returned stream.
    /// </summary>
    Task<Stream> OpenReadStreamAsync(string key, CancellationToken ct = default);
}

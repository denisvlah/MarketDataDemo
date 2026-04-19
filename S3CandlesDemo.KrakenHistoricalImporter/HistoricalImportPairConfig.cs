using Amazon.S3;
using Amazon.S3.Model;

namespace S3CandlesDemo.KrakenHistoricalImporter;

/// <summary>
/// Represents a single row from the historical import config CSV.
/// Same format as KrakenLatestCollector: AssetPair,KrakenPair,IntervalMinutes,StartDate
/// </summary>
public record ImportJobConfig(
    string AssetPair,      // Canonical name for S3 storage (e.g. "BTCUSD")
    string KrakenPair,     // Pair name in Kraken archive filenames (e.g. "XBTUSD")
    int IntervalMinutes,   // Candle interval in minutes
    DateTime StartDate     // Earliest date to import from
);

/// <summary>
/// Reads and parses the import config CSV. Same format as KrakenLatestCollector's CsvConfigReader.
/// Supports loading from a local file or from an S3 bucket.
/// </summary>
public static class ImportConfigReader
{
    private static readonly HashSet<int> ValidIntervals = new() { 1, 5, 15, 30, 60, 240, 720, 1440 };

    public static List<ImportJobConfig> ReadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Config CSV not found: {filePath}");

        var lines = File.ReadAllLines(filePath);
        return ParseLines(lines);
    }

    public static async Task<List<ImportJobConfig>> ReadFromS3Async(
        IAmazonS3 s3Client, string bucket, string key, CancellationToken ct = default)
    {
        var response = await s3Client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key
        }, ct);

        using var reader = new StreamReader(response.ResponseStream);
        var content = await reader.ReadToEndAsync(ct);
        var lines = content.Split('\n', StringSplitOptions.None);
        return ParseLines(lines);
    }

    public static List<ImportJobConfig> ParseLines(IEnumerable<string> lines)
    {
        var result = new List<ImportJobConfig>();
        int lineNum = 0;

        foreach (var rawLine in lines)
        {
            lineNum++;
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split(',');
            if (parts.Length != 4)
                throw new FormatException($"Line {lineNum}: expected 4 columns, got {parts.Length}. Line: '{line}'");

            var assetPair = parts[0].Trim();
            var krakenPair = parts[1].Trim();

            if (!int.TryParse(parts[2].Trim(), out var interval))
                throw new FormatException($"Line {lineNum}: invalid interval '{parts[2].Trim()}'");

            if (!ValidIntervals.Contains(interval))
                throw new FormatException($"Line {lineNum}: interval {interval} is not available in Kraken archives. Valid values: {string.Join(", ", ValidIntervals.OrderBy(x => x))}");

            if (!DateTime.TryParseExact(parts[3].Trim(), "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var startDate))
                throw new FormatException($"Line {lineNum}: invalid date '{parts[3].Trim()}', expected yyyy-MM-dd");

            result.Add(new ImportJobConfig(assetPair, krakenPair, interval, startDate));
        }

        return result;
    }
}

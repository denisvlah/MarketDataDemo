using Amazon.S3;
using Amazon.S3.Model;

namespace MarketDataDemo.Candles;

/// <summary>
/// Reads and parses the job config CSV (kraken-collector-config.csv) into PairJobConfig entries.
/// Supports loading from a local file or from an S3 bucket.
/// </summary>
public static class PairJobConfigReader
{
    private static readonly HashSet<int> ValidIntervals = new() { 1, 5, 15, 30, 60, 240, 1440, 10080, 21600 };

    public static List<PairJobConfig> ReadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Config CSV not found: {filePath}");

        var lines = File.ReadAllLines(filePath);
        return ParseLines(lines);
    }

    public static async Task<List<PairJobConfig>> ReadFromS3Async(
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

    public static List<PairJobConfig> ParseLines(IEnumerable<string> lines)
    {
        var result = new List<PairJobConfig>();
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
                throw new FormatException($"Line {lineNum}: interval {interval} is not a valid Kraken interval. Valid values: {string.Join(", ", ValidIntervals.OrderBy(x => x))}");

            if (!DateTime.TryParseExact(parts[3].Trim(), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var startDate))
                throw new FormatException($"Line {lineNum}: invalid date '{parts[3].Trim()}', expected yyyy-MM-dd");

            result.Add(new PairJobConfig(assetPair, krakenPair, interval, startDate));
        }

        return result;
    }
}

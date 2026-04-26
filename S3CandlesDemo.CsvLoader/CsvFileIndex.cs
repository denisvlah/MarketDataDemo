using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;

namespace S3CandlesDemo.CsvLoader;

/// <summary>
/// Lists and parses CSV file names from an S3 bucket.
/// CSV file naming convention: {Symbol}_{IntervalMinutes}_{StartDateTime}_{EndDateTime}.csv
/// DateTime format: yyyy-MM-dd HH:mm:ss
/// </summary>
public static partial class CsvFileIndex
{
    // Matches: ETHEUR_60_2024-01-01 00:00:00_2024-12-31 23:59:59.csv
    [GeneratedRegex(@"^(?<symbol>[A-Za-z]+)_(?<interval>\d+)_(?<start>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})_(?<end>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.csv$")]
    private static partial Regex CsvFilePattern();

    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// Lists all CSV files in the given S3 bucket and parses their metadata from file names.
    /// Files that don't match the expected naming convention are skipped.
    /// </summary>
    public static async Task<List<CsvFileInfo>> ListCsvFilesAsync(
        IAmazonS3 s3Client, string csvBucket, ILogger logger, CancellationToken ct = default)
    {
        var result = new List<CsvFileInfo>();
        string? continuationToken = null;

        do
        {
            var response = await s3Client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = csvBucket,
                Prefix = "csv/",
                ContinuationToken = continuationToken
            }, ct);

            foreach (var obj in response.S3Objects)
            {
                // Extract the file name from the key (strip any prefix/folder)
                var fileName = Path.GetFileName(obj.Key);
                var match = CsvFilePattern().Match(fileName);
                if (!match.Success)
                {
                    logger.LogDebug("Skipping non-matching CSV key: {Key}", obj.Key);
                    continue;
                }

                var symbol = match.Groups["symbol"].Value;
                var interval = int.Parse(match.Groups["interval"].Value);
                var start = DateTime.ParseExact(match.Groups["start"].Value, DateFormat, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
                var end = DateTime.ParseExact(match.Groups["end"].Value, DateFormat, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

                result.Add(new CsvFileInfo(obj.Key, symbol, interval, start, end));
            }

            continuationToken = response.IsTruncated ? response.NextContinuationToken : null;
        } while (continuationToken != null);

        logger.LogInformation("Found {Count} CSV files in bucket '{Bucket}'", result.Count, csvBucket);
        return result;
    }
}

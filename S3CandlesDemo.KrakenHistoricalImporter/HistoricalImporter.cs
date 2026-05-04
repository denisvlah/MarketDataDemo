using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;

namespace S3CandlesDemo.KrakenHistoricalImporter;

/// <summary>
/// Downloads all quarterly OHLCVT archives from Kraken's Google Drive folder, extracts their CSV
/// files, renames them to the csv loader naming convention ({Symbol}_{Interval}_{Start}_{End}.csv),
/// and uploads them to the S3 csv/ folder.
/// </summary>
public partial class HistoricalImporter
{
    private readonly IAmazonS3 _s3Client;
    private readonly HttpClient _httpClient;
    private readonly string _bucket;
    private readonly ILogger<HistoricalImporter> _logger;

    // Kraken's quarterly OHLCVT archive folder on Google Drive
    private const string DefaultFolderId = "15RSlNuW_h0kVM8or8McOGOMfHeBFvFGI";

    // DateTime format used in the csv loader file naming convention
    private const string CsvDateFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Matches quarterly archive ZIPs: Kraken_OHLCVT_Q{quarter}_{year}.zip
    [GeneratedRegex(@"^Kraken_OHLCVT_Q(\d)_(\d{4})\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveFileNameRegex();

    // Matches CSV files inside archives: {KrakenPair}_{IntervalMinutes}.csv
    [GeneratedRegex(@"^([A-Za-z0-9]+)_(\d+)\.csv$")]
    private static partial Regex CsvFileNameRegex();

    public HistoricalImporter(
        IAmazonS3 s3Client,
        HttpClient httpClient,
        string bucket,
        ILogger<HistoricalImporter> logger)
    {
        _s3Client = s3Client;
        _httpClient = httpClient;
        _bucket = bucket;
        _logger = logger;
    }

    /// <summary>
    /// Downloads all quarterly archives, extracts CSV files, and uploads them to S3.
    /// Returns true if all archives were processed successfully.
    /// </summary>
    public async Task<bool> RunAllAsync(
        string tempDir, string googleApiKey, string? folderId = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(tempDir);
        folderId ??= DefaultFolderId;

        // Build an in-memory set of already-uploaded CSV keys to avoid redundant uploads
        var existingKeys = await ListExistingCsvKeysAsync(ct);

        _logger.LogInformation("Listing files in Google Drive folder {FolderId}...", folderId);
        var folderFiles = await GoogleDriveDownloader.ListFolderAsync(_httpClient, folderId, googleApiKey, _logger, ct);

        var archives = folderFiles
            .Where(kv => ArchiveFileNameRegex().IsMatch(kv.Key))
            .OrderBy(kv => kv.Key)
            .ToList();

        if (archives.Count == 0)
        {
            _logger.LogWarning("No quarterly archive ZIP files found in Google Drive folder {FolderId}.", folderId);
            return true;
        }

        _logger.LogInformation("Found {Count} quarterly archives to process.", archives.Count);

        bool allSuccess = true;

        foreach (var (fileName, fileId) in archives)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ProcessArchiveAsync(fileName, fileId, tempDir, googleApiKey, existingKeys, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process archive {Archive}.", fileName);
                allSuccess = false;
            }
        }

        return allSuccess;
    }

    private async Task ProcessArchiveAsync(
        string fileName, string fileId, string tempDir, string googleApiKey,
        HashSet<string> existingKeys, CancellationToken ct)
    {
        var zipPath = Path.Combine(tempDir, fileName);
        var extractDir = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(fileName));

        // Download archive — skip if already cached and valid
        if (File.Exists(zipPath) && !GoogleDriveDownloader.IsValidZip(zipPath))
        {
            _logger.LogWarning("Cached archive {Archive} is corrupt, re-downloading.", fileName);
            File.Delete(zipPath);
        }

        if (!File.Exists(zipPath))
            await GoogleDriveDownloader.DownloadAsync(_httpClient, fileId, zipPath, _logger, googleApiKey, ct);
        else
            _logger.LogInformation("Archive {Archive} already downloaded, reusing.", fileName);

        // Extract archive — skip if already extracted
        if (!Directory.Exists(extractDir) || !Directory.EnumerateFiles(extractDir, "*.csv", SearchOption.AllDirectories).Any())
            GoogleDriveDownloader.ExtractZip(zipPath, extractDir, _logger);

        var csvFiles = Directory.EnumerateFiles(extractDir, "*.csv", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();

        _logger.LogInformation("Processing {Count} CSV files from {Archive}.", csvFiles.Count, fileName);

        int uploaded = 0, skipped = 0;
        foreach (var csvPath in csvFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (await UploadCsvAsync(csvPath, existingKeys, ct))
                uploaded++;
            else
                skipped++;
        }

        _logger.LogInformation("{Archive}: {Uploaded} uploaded, {Skipped} skipped (already in S3).",
            fileName, uploaded, skipped);
    }

    /// <summary>
    /// Determines the canonical S3 key for the CSV, checks if it already exists, and uploads if not.
    /// Returns true if the file was uploaded.
    /// </summary>
    private async Task<bool> UploadCsvAsync(string csvPath, HashSet<string> existingKeys, CancellationToken ct)
    {
        var csvFileName = Path.GetFileName(csvPath);
        var match = CsvFileNameRegex().Match(csvFileName);
        if (!match.Success)
        {
            _logger.LogDebug("Skipping file with unrecognized name: {File}", csvFileName);
            return false;
        }

        var krakenPair = match.Groups[1].Value;
        var intervalStr = match.Groups[2].Value;

        var range = await GetTimestampRangeAsync(csvPath, ct);
        if (range is null)
        {
            _logger.LogWarning("CSV file {File} is empty, skipping.", csvFileName);
            return false;
        }

        var (first, last) = range.Value;
        var newFileName = $"{krakenPair}_{intervalStr}_{first.ToString(CsvDateFormat)}_{last.ToString(CsvDateFormat)}.csv";
        var s3Key = $"csv/{newFileName}";

        if (existingKeys.Contains(s3Key))
        {
            _logger.LogDebug("Already in S3: {Key}", s3Key);
            return false;
        }

        _logger.LogInformation("Uploading {File} → {Key}...", csvFileName, s3Key);

        await using var fileStream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await _s3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = s3Key,
            InputStream = fileStream,
            ContentType = "text/csv"
        }, ct);

        existingKeys.Add(s3Key);
        _logger.LogInformation("Uploaded: {Key}", s3Key);
        return true;
    }

    /// <summary>
    /// Reads the first and last Unix timestamp from a Kraken CSV file (format: timestamp,o,h,l,c,v,trades).
    /// Returns null if the file is empty.
    /// </summary>
    private static async Task<(DateTime First, DateTime Last)?> GetTimestampRangeAsync(
        string csvPath, CancellationToken ct)
    {
        string? firstLine;
        using (var reader = new StreamReader(csvPath))
            firstLine = await reader.ReadLineAsync(ct);

        if (string.IsNullOrWhiteSpace(firstLine))
            return null;

        var first = ParseUnixTimestamp(firstLine);
        var last = ReadLastTimestamp(csvPath);
        return (first, last);
    }

    /// <summary>
    /// Efficiently reads the last timestamp by seeking to the last 512 bytes of the file.
    /// </summary>
    private static DateTime ReadLastTimestamp(string csvPath)
    {
        using var fs = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // 512 bytes is well beyond the length of any single Kraken CSV row
        const int blockSize = 512;
        long startPos = Math.Max(0, fs.Length - blockSize);
        fs.Seek(startPos, SeekOrigin.Begin);

        var buffer = new byte[fs.Length - startPos];
        fs.ReadExactly(buffer);

        var text = Encoding.ASCII.GetString(buffer).TrimEnd('\r', '\n');
        var lastNewline = text.LastIndexOfAny(['\n', '\r']);
        var lastLine = lastNewline >= 0 ? text[(lastNewline + 1)..] : text;

        return ParseUnixTimestamp(lastLine);
    }

    private static DateTime ParseUnixTimestamp(string line)
    {
        var commaIdx = line.IndexOf(',');
        var part = commaIdx >= 0 ? line[..commaIdx] : line;
        var seconds = long.Parse(part.Trim(), CultureInfo.InvariantCulture);
        return UnixEpoch.AddSeconds(seconds);
    }

    /// <summary>
    /// Lists all CSV object keys already present in the S3 csv/ folder.
    /// </summary>
    private async Task<HashSet<string>> ListExistingCsvKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? continuationToken = null;

        do
        {
            var response = await _s3Client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = "csv/",
                ContinuationToken = continuationToken
            }, ct);

            foreach (var obj in response.S3Objects ?? [])
                keys.Add(obj.Key);

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken != null);

        _logger.LogInformation("Found {Count} existing CSV files in S3 csv/ folder.", keys.Count);
        return keys;
    }
}

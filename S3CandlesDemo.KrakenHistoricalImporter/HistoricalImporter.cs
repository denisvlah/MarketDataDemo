using System.Text.RegularExpressions;
using S3CandlesDemo.Candles;

namespace S3CandlesDemo.KrakenHistoricalImporter;

/// <summary>
/// Represents a quarterly archive available on Google Drive.
/// </summary>
public record QuarterlyArchive(int Year, int Quarter, string FileName, string GoogleDriveFileId)
{
    public DateTime QuarterStart => new(Year, (Quarter - 1) * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public DateTime QuarterEnd => QuarterStart.AddMonths(3);
}

/// <summary>
/// Orchestrates historical candle import from Kraken's quarterly Google Drive archives.
/// Downloads only the ZIP files needed, extracts the relevant CSVs, and imports only missing candles.
/// </summary>
public partial class HistoricalImporter
{
    private readonly ICandlesRepository _repo;
    private readonly HttpClient _httpClient;
    private readonly ILogger<HistoricalImporter> _logger;

    // Kraken's quarterly OHLCVT archive folder on Google Drive
    private const string DefaultFolderId = "15RSlNuW_h0kVM8or8McOGOMfHeBFvFGI";

    // Regex to parse quarterly archive filenames: Kraken_OHLCVT_Q{quarter}_{year}.zip
    [GeneratedRegex(@"^Kraken_OHLCVT_Q(\d)_(\d{4})\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveFileNameRegex();

    // Valid intervals available in Kraken archives
    private static readonly HashSet<int> ValidArchiveIntervals = new() { 1, 5, 15, 30, 60, 240, 720, 1440 };

    public HistoricalImporter(
        ICandlesRepository repo,
        HttpClient httpClient,
        ILogger<HistoricalImporter> logger)
    {
        _repo = repo;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Run all import jobs. Returns true if all succeeded, false if any failed.
    /// </summary>
    public async Task<bool> RunAllAsync(
        IReadOnlyList<ImportJobConfig> jobs, string tempDir, string googleApiKey,
        string? folderId = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(tempDir);
        folderId ??= DefaultFolderId;

        // List available quarterly archives from Google Drive
        _logger.LogInformation("Listing quarterly archives in Google Drive folder {FolderId}...", folderId);
        var folderFiles = await GoogleDriveDownloader.ListFolderAsync(_httpClient, folderId, googleApiKey, _logger, ct);

        var archives = ParseArchiveList(folderFiles);
        if (archives.Count == 0)
        {
            _logger.LogWarning("No quarterly archive files found in Google Drive folder.");
            return true;
        }

        _logger.LogInformation("Found {Count} quarterly archives: {Range}",
            archives.Count,
            $"Q{archives.First().Quarter}/{archives.First().Year} — Q{archives.Last().Quarter}/{archives.Last().Year}");

        bool allSuccess = true;

        foreach (var job in jobs)
        {
            try
            {
                await ImportJobAsync(job, archives, tempDir, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Import failed: {AssetPair}/{Interval}", job.AssetPair, job.IntervalMinutes);
                allSuccess = false;
            }
        }

        return allSuccess;
    }

    /// <summary>
    /// Import a single pair/interval: detect gaps in stored data and fill them from quarterly archives.
    /// Handles the case where the latest collector already stored recent data — the historical importer
    /// will fill the gap between the configured start date and the oldest stored data, as well as
    /// continue importing after the latest stored data.
    /// </summary>
    public async Task ImportJobAsync(
        ImportJobConfig job, List<QuarterlyArchive> archives, string tempDir, CancellationToken ct = default)
    {
        if (!ValidArchiveIntervals.Contains(job.IntervalMinutes))
        {
            _logger.LogWarning("Interval {Interval} is not available in Kraken archives. Skipping {AssetPair}.",
                job.IntervalMinutes, job.AssetPair);
            return;
        }

        _logger.LogInformation("Processing {AssetPair}/{Interval} (start: {StartDate})...",
            job.AssetPair, job.IntervalMinutes, job.StartDate);

        // Determine what we already have in S3
        var existingFiles = await _repo.GetCandleFilesAsync(job.AssetPair, job.IntervalMinutes, ct);

        // Find gaps in coverage that need filling
        var gaps = FindGaps(job.StartDate, existingFiles);

        if (gaps.Count == 0)
        {
            _logger.LogInformation("No gaps found for {AssetPair}/{Interval}. Already up to date.",
                job.AssetPair, job.IntervalMinutes);
            return;
        }

        _logger.LogInformation("{AssetPair}/{Interval}: found {GapCount} gap(s) to fill",
            job.AssetPair, job.IntervalMinutes, gaps.Count);

        foreach (var gap in gaps)
        {
            _logger.LogInformation("{AssetPair}/{Interval}: filling gap {GapStart} — {GapEnd}",
                job.AssetPair, job.IntervalMinutes,
                gap.Start.ToString("yyyy-MM-dd HH:mm"),
                gap.End == DateTime.MaxValue ? "open" : gap.End.ToString("yyyy-MM-dd HH:mm"));

            // Find which quarterly archives cover this gap
            var neededArchives = archives
                .Where(a => a.QuarterEnd > gap.Start
                         && a.QuarterStart >= GetQuarterStart(gap.Start)
                         && (gap.End == DateTime.MaxValue || a.QuarterStart < gap.End))
                .OrderBy(a => a.QuarterStart)
                .ToList();

            if (neededArchives.Count == 0)
                continue;

            _logger.LogInformation("{AssetPair}/{Interval}: need {Count} quarterly archives for this gap",
                job.AssetPair, job.IntervalMinutes, neededArchives.Count);

            foreach (var archive in neededArchives)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await ImportFromArchiveAsync(job, archive, gap.Start, gap.End, tempDir, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to import {AssetPair}/{Interval} from {Archive}",
                        job.AssetPair, job.IntervalMinutes, archive.FileName);
                    throw;
                }
            }
        }

        _logger.LogInformation("Import complete for {AssetPair}/{Interval}.", job.AssetPair, job.IntervalMinutes);
    }

    /// <summary>
    /// Find time ranges that are not covered by existing stored files.
    /// Returns a list of (Start, End) gaps where End == DateTime.MaxValue means open-ended (load all available).
    /// </summary>
    internal static List<(DateTime Start, DateTime End)> FindGaps(
        DateTime configStart, IReadOnlyList<CandleFileInfo> files)
    {
        var gaps = new List<(DateTime Start, DateTime End)>();

        if (files.Count == 0)
        {
            // No data at all — everything from configStart onward is a gap
            gaps.Add((configStart, DateTime.MaxValue));
            return gaps;
        }

        var earliestStored = files.Min(f => f.Start);
        var latestStored = files.Max(f => f.End);

        // Gap before the oldest stored data
        if (configStart < earliestStored)
            gaps.Add((configStart, earliestStored));

        // Gap after the latest stored data (open-ended)
        gaps.Add((latestStored, DateTime.MaxValue));

        return gaps;
    }

    /// <summary>
    /// Download a single quarterly archive (if not already cached), extract the relevant CSV,
    /// filter to only candles within the gap range (after gapStart, before gapEnd), and store to S3.
    /// </summary>
    private async Task ImportFromArchiveAsync(
        ImportJobConfig job, QuarterlyArchive archive, DateTime gapStart, DateTime gapEnd,
        string tempDir, CancellationToken ct)
    {
        var zipPath = Path.Combine(tempDir, archive.FileName);
        var extractDir = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(archive.FileName));

        // Download if not already present
        if (!File.Exists(zipPath))
        {
            _logger.LogInformation("Downloading {Archive}...", archive.FileName);
            await GoogleDriveDownloader.DownloadAsync(_httpClient, archive.GoogleDriveFileId, zipPath, _logger, ct);
        }
        else
        {
            _logger.LogInformation("Archive {Archive} already downloaded, reusing.", archive.FileName);
        }

        // Extract if not already done
        if (!Directory.Exists(extractDir) || !Directory.EnumerateFiles(extractDir, "*.csv", SearchOption.AllDirectories).Any())
        {
            GoogleDriveDownloader.ExtractZip(zipPath, extractDir, _logger);
        }

        // Find the CSV for this pair/interval
        var csvFileName = $"{job.KrakenPair}_{job.IntervalMinutes}.csv";
        var csvPath = Directory.EnumerateFiles(extractDir, csvFileName, SearchOption.AllDirectories).FirstOrDefault();

        if (csvPath == null)
        {
            _logger.LogWarning("CSV {FileName} not found in {Archive}. Skipping.",
                csvFileName, archive.FileName);
            return;
        }

        // Parse and filter: only candles within the gap range
        var candles = KrakenCsvParser.ParseFileAsync(csvPath, ct)
            .Where(c => c.Timestamp > gapStart && (gapEnd == DateTime.MaxValue || c.Timestamp < gapEnd));

        _logger.LogInformation("Importing candles from {Archive} for {AssetPair}/{Interval} ({After} — {Before})...",
            archive.FileName, job.AssetPair, job.IntervalMinutes,
            gapStart.ToString("yyyy-MM-dd HH:mm"),
            gapEnd == DateTime.MaxValue ? "open" : gapEnd.ToString("yyyy-MM-dd HH:mm"));

        await _repo.StoreCandlesAsync(job.AssetPair, job.IntervalMinutes, candles, ct);
    }

    /// <summary>
    /// Parses the Google Drive folder listing into a sorted list of quarterly archives.
    /// </summary>
    internal static List<QuarterlyArchive> ParseArchiveList(Dictionary<string, string> folderFiles)
    {
        var archives = new List<QuarterlyArchive>();

        foreach (var (fileName, fileId) in folderFiles)
        {
            var match = ArchiveFileNameRegex().Match(fileName);
            if (!match.Success) continue;

            var quarter = int.Parse(match.Groups[1].Value);
            var year = int.Parse(match.Groups[2].Value);

            if (quarter is < 1 or > 4) continue;

            archives.Add(new QuarterlyArchive(year, quarter, fileName, fileId));
        }

        return archives.OrderBy(a => a.QuarterStart).ToList();
    }

    /// <summary>
    /// Returns the start of the quarter containing the given date.
    /// </summary>
    private static DateTime GetQuarterStart(DateTime date)
    {
        var quarterMonth = ((date.Month - 1) / 3) * 3 + 1;
        return new DateTime(date.Year, quarterMonth, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}

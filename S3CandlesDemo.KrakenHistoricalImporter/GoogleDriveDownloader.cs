using System.IO.Compression;
using System.Text.Json;

namespace S3CandlesDemo.KrakenHistoricalImporter;

/// <summary>
/// Downloads files from Google Drive and manages ZIP extraction.
/// Uses the Google Drive API v3 to list files in a public folder (requires an API key).
/// </summary>
public static class GoogleDriveDownloader
{
    /// <summary>
    /// Lists files in a public Google Drive folder using the Drive API v3.
    /// Returns a dictionary of filename -> file ID.
    /// Requires a Google API key (free, from Google Cloud Console).
    /// </summary>
    public static async Task<Dictionary<string, string>> ListFolderAsync(
        HttpClient httpClient, string folderId, string apiKey, ILogger logger, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pageToken = null;

        do
        {
            var url = $"https://www.googleapis.com/drive/v3/files" +
                      $"?q=%27{folderId}%27+in+parents" +
                      $"&key={apiKey}" +
                      $"&fields=nextPageToken,files(id,name)" +
                      $"&pageSize=100";

            if (pageToken != null)
                url += $"&pageToken={pageToken}";

            using var response = await httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("files", out var files))
            {
                foreach (var file in files.EnumerateArray())
                {
                    var name = file.GetProperty("name").GetString();
                    var id = file.GetProperty("id").GetString();
                    if (name != null && id != null)
                        result[name] = id;
                }
            }

            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var npt) ? npt.GetString() : null;
        } while (pageToken != null);

        logger.LogInformation("Listed {Count} files in Google Drive folder {FolderId}", result.Count, folderId);
        return result;
    }

    /// <summary>
    /// Downloads a file from Google Drive by file ID and saves it to the specified path.
    /// Uses the direct download URL format for large files.
    /// </summary>
    public static async Task DownloadAsync(HttpClient httpClient, string fileId, string destinationPath, ILogger logger, CancellationToken ct = default)
    {
        var url = $"https://drive.google.com/uc?export=download&id={fileId}&confirm=t";

        logger.LogInformation("Downloading Google Drive file {FileId} to {Path}...", fileId, destinationPath);

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        var lastLog = DateTime.UtcNow;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(10))
            {
                if (totalBytes.HasValue)
                    logger.LogInformation("Download progress: {Downloaded:F1} MB / {Total:F1} MB ({Pct:F1}%)",
                        totalRead / 1048576.0, totalBytes.Value / 1048576.0, totalRead * 100.0 / totalBytes.Value);
                else
                    logger.LogInformation("Download progress: {Downloaded:F1} MB", totalRead / 1048576.0);

                lastLog = DateTime.UtcNow;
            }
        }

        logger.LogInformation("Download complete: {TotalMB:F1} MB", totalRead / 1048576.0);
    }

    /// <summary>
    /// Extracts a ZIP archive to the specified directory.
    /// </summary>
    public static void ExtractZip(string zipPath, string extractDir, ILogger logger)
    {
        logger.LogInformation("Extracting {Zip} to {Dir}...", zipPath, extractDir);
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        logger.LogInformation("Extraction complete.");
    }
}

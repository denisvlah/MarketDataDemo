using Amazon.S3;
using Amazon.S3.Model;

namespace MarketDataDemo.CsvLoader;

/// <summary>
/// <see cref="ICsvSource"/> implementation backed by an S3 bucket.
/// </summary>
public class S3CsvSource : ICsvSource
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _csvBucket;
    private readonly ILogger<S3CsvSource> _logger;

    public S3CsvSource(IAmazonS3 s3Client, string csvBucket, ILogger<S3CsvSource> logger)
    {
        _s3Client = s3Client;
        _csvBucket = csvBucket;
        _logger = logger;
    }

    public Task<List<CsvFileInfo>> ListFilesAsync(CancellationToken ct = default)
        => CsvFileIndex.ListCsvFilesAsync(_s3Client, _csvBucket, _logger, ct);

    public async Task<Stream> OpenReadStreamAsync(string key, CancellationToken ct = default)
    {
        var response = await _s3Client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _csvBucket,
            Key = key
        }, ct);
        return response.ResponseStream;
    }
}

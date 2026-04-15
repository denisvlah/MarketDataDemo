using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace S3CandlesDemo.Candles
{
    public class S3CandlesRepository : CandlesRepositoryBase
    {
        public override async Task<IReadOnlyList<CandleFileInfo>> GetCandleFilesAsync(string symbol, int intervalMinutes, CancellationToken cancellationToken = default)
        {
            // Rebuild index to ensure up-to-date
            BuildFileIndex();
            return await base.GetCandleFilesAsync(symbol, intervalMinutes, cancellationToken);
        }

        public override async Task RemoveCandleFilesAsync(string symbol, int intervalMinutes, CancellationToken cancellationToken = default)
        {
            var files = await GetCandleFilesAsync(symbol, intervalMinutes, cancellationToken);
            foreach (var file in files) await RemoveCandleFileAsync(file, cancellationToken);
        }

        public override async Task RemoveCandleFileAsync(CandleFileInfo fileInfo, CancellationToken cancellationToken = default)
        {
            var key = fileInfo.Path;
            if (!string.IsNullOrEmpty(_prefix) && !key.StartsWith(_prefix))
                key = KeyFromFileName(Path.GetFileName(fileInfo.Path));
            await _s3Client.DeleteObjectAsync(_bucket, key, cancellationToken);
            foreach (var kvp in _fileIndex)
                kvp.Value.RemoveAll(f => f.Path == fileInfo.Path);
        }
        private readonly IAmazonS3 _s3Client;
        private readonly string _bucket;
        private readonly string? _prefix;

        public S3CandlesRepository(string bucket, string? prefix = null, IAmazonS3? client = null) : base(prefix ?? string.Empty)
        {
            _bucket = bucket;
            _prefix = prefix?.Trim('/');
            _s3Client = client ?? new AmazonS3Client();
            BuildFileIndex();
        }

        private string KeyFromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(_prefix)) return fileName;
            return $"{_prefix}/{fileName}";
        }

        protected override IEnumerable<string> EnumerateFiles()
        {
            var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = _prefix };
            ListObjectsV2Response? response;
            do
            {
                response = _s3Client.ListObjectsV2Async(request).GetAwaiter().GetResult();
                foreach (var obj in response.S3Objects)
                    if (obj.Key.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                        yield return obj.Key;
                request.ContinuationToken = response.NextContinuationToken;
            } while (response.IsTruncated);
        }

        protected override string GetFileName(string filePathOrKey)
        {
            if (string.IsNullOrEmpty(filePathOrKey)) return string.Empty;
            var idx = filePathOrKey.LastIndexOf('/');
            return idx >= 0 ? filePathOrKey.Substring(idx + 1) : filePathOrKey;
        }

        protected override Task<Stream> OpenWriteStreamAsync(string tempPath)
        {
            var tmp = Path.Combine(Path.GetTempPath(), Path.GetFileName(tempPath));
            Stream s = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
            return Task.FromResult(s);
        }

        protected override async Task MoveTempToFinalAsync(string tempPath, string finalPath)
        {
            // tempPath passed from base is the relative temp name; the actual written file lives in system temp directory
            var actualTemp = Path.Combine(Path.GetTempPath(), Path.GetFileName(tempPath));
            var fileName = Path.GetFileName(finalPath);
            var key = KeyFromFileName(fileName);
            using var transfer = new TransferUtility(_s3Client);
            await transfer.UploadAsync(actualTemp, _bucket, key);
            try { File.Delete(actualTemp); }
            catch
            {
                // ignored
            }
        }


        protected override async Task<Stream> OpenReadStreamAsync(string filePathOrKey, long offset)
        {
            var key = filePathOrKey;
            // If stored keys are full S3 keys, use them directly. Otherwise, combine prefix.
            if (!string.IsNullOrEmpty(_prefix) && !filePathOrKey.StartsWith(_prefix))
                key = KeyFromFileName(GetFileName(filePathOrKey));

            var request = new GetObjectRequest { BucketName = _bucket, Key = key };
            if (offset > 0)
                request.ByteRange = new ByteRange(offset, long.MaxValue);

            var response = await _s3Client.GetObjectAsync(request);

            return response.ResponseStream;
        }
    }
}


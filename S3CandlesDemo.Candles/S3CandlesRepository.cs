using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace S3CandlesDemo.Candles
{
    public class S3CandlesRepository : CandlesRepositoryBase
    {
        // Streams candles directly to S3 via multipart upload — no full buffering in memory or on disk.
        // Only a ~5MB part buffer is held at any time, enabling gigabyte-scale streams.
        public override async Task StoreCandlesAsync(string symbol, int intervalMinutes, IAsyncEnumerable<Candle> candles, CancellationToken cancellationToken = default)
        {
            const int minPartSize = 5 * 1024 * 1024; // 5MB — S3 minimum part size

            // Upload to a temp key first; we don't know the final filename until all candles are consumed (need min/max timestamps)
            string tempKey = KeyFromFileName($"{symbol}_{intervalMinutes}_{Guid.NewGuid()}.tmp");

            var initResponse = await _s3Client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = tempKey
            }, cancellationToken);
            string uploadId = initResponse.UploadId;

            var partETags = new List<PartETag>();
            DateTime? minTimestamp = null, maxTimestamp = null;
            byte[] candleBuffer = new byte[Candle.CandleByteSize];
            var partStream = new MemoryStream(minPartSize + Candle.CandleByteSize);
            int partNumber = 1;

            try
            {
                await foreach (var candle in candles.WithCancellation(cancellationToken))
                {
                    Candle.CandleToBytes(candle, candleBuffer);
                    partStream.Write(candleBuffer, 0, candleBuffer.Length);

                    if (!minTimestamp.HasValue || candle.Timestamp < minTimestamp.Value)
                        minTimestamp = candle.Timestamp;
                    if (!maxTimestamp.HasValue || candle.Timestamp > maxTimestamp.Value)
                        maxTimestamp = candle.Timestamp;

                    if (partStream.Position >= minPartSize)
                    {
                        partETags.Add(await UploadPartAsync(tempKey, uploadId, partNumber, partStream, cancellationToken));
                        partNumber++;
                        partStream.SetLength(0);
                    }
                }

                // No candles received — abort
                if (!minTimestamp.HasValue || !maxTimestamp.HasValue)
                {
                    await _s3Client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                    {
                        BucketName = _bucket, Key = tempKey, UploadId = uploadId
                    }, cancellationToken);
                    return;
                }

                // Upload remaining data as the final part
                if (partStream.Position > 0)
                {
                    partETags.Add(await UploadPartAsync(tempKey, uploadId, partNumber, partStream, cancellationToken));
                }

                await _s3Client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                {
                    BucketName = _bucket,
                    Key = tempKey,
                    UploadId = uploadId,
                    PartETags = partETags
                }, cancellationToken);
            }
            catch
            {
                await _s3Client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = _bucket, Key = tempKey, UploadId = uploadId
                }, CancellationToken.None);
                throw;
            }
            finally
            {
                partStream.Dispose();
            }

            // Compute the final key name now that we know the time range
            var key = (symbol, intervalMinutes);
            var start = minTimestamp.Value;
            var end = maxTimestamp.Value;
            int newVersion = 1;
            if (_fileIndex.TryGetValue(key, out var existingFiles))
            {
                var intersecting = existingFiles.Where(f => f.End >= start && f.Start <= end);
                if (intersecting.Any())
                    newVersion = intersecting.Max(f => f.Version) + 1;
            }

            string newFileName = $"{symbol}_{intervalMinutes}_{start:yyyyMMdd'T'HHmmss}_{end:yyyyMMdd'T'HHmmss}_v{newVersion}.bin";
            var finalKey = KeyFromFileName(newFileName);

            // Server-side copy to final key, then delete temp (no data re-transfer)
            await _s3Client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = tempKey,
                DestinationBucket = _bucket,
                DestinationKey = finalKey
            }, cancellationToken);
            await _s3Client.DeleteObjectAsync(_bucket, tempKey, cancellationToken);

            string fullPath = string.IsNullOrEmpty(_prefix) ? newFileName : $"{_prefix}/{newFileName}";
            var info = new CandleFileInfoInternal(fullPath, start, end, newVersion);
            _fileIndex.AddOrUpdate(key, k => new List<CandleFileInfoInternal> { info }, (k, list) => { list.Add(info); list.Sort((a, b) => a.Start.CompareTo(b.Start)); return list; });
        }

        private async Task<PartETag> UploadPartAsync(string key, string uploadId, int partNumber, MemoryStream partStream, CancellationToken cancellationToken)
        {
            long partSize = partStream.Position;
            partStream.Position = 0;
            var response = await _s3Client.UploadPartAsync(new UploadPartRequest
            {
                BucketName = _bucket,
                Key = key,
                UploadId = uploadId,
                PartNumber = partNumber,
                InputStream = partStream,
                PartSize = partSize
            }, cancellationToken);
            return new PartETag(partNumber, response.ETag);
        }

        protected override async Task<long> GetFileSizeAsync(string path)
        {
            try
            {
                var key = path;
                if (!string.IsNullOrEmpty(_prefix) && !path.StartsWith(_prefix))
                    key = KeyFromFileName(GetFileName(path));
                var meta = await _s3Client.GetObjectMetadataAsync(_bucket, key);
                return meta.ContentLength;
            }
            catch { return 0; }
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
            BuildFileIndexAsync().GetAwaiter().GetResult();
        }

        private string KeyFromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(_prefix)) return fileName;
            return $"{_prefix}/{fileName}";
        }

        protected override async IAsyncEnumerable<string> EnumerateFilesAsync()
        {
            var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = _prefix };
            ListObjectsV2Response? response;
            do
            {
                response = await _s3Client.ListObjectsV2Async(request);
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

        public override async Task<IReadOnlyList<PairJobConfig>> GetJobConfigAsync(CancellationToken cancellationToken = default)
        {
            var configs = await PairJobConfigReader.ReadFromS3Async(_s3Client, _bucket, ConfigKey, cancellationToken);
            return configs;
        }
    }
}


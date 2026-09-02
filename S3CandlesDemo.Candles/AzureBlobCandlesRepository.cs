using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace S3CandlesDemo.Candles
{
    /// <summary>
    /// Azure Blob Storage backend for candle storage and retrieval.
    /// Streams candle data directly to Azure via a <see cref="BlockBlobClient"/> block-commit
    /// pattern: the write end of a <see cref="Pipe"/> is returned to the caller while a
    /// background task stages blocks from the read end. <see cref="MoveTempToFinalAsync"/>
    /// awaits the upload then does a server-side blob copy to the final name — no local disk I/O.
    /// </summary>
    public class AzureBlobCandlesRepository : CandlesRepositoryBase
    {
        private readonly BlobContainerClient _container;
        private readonly string? _prefix;

        // Tracks in-flight Azure block blob uploads keyed by the abstract temp path.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Task UploadTask, string TempBlobName)> _pendingUploads = new();

        // Upload pipe back-pressure: pause at 1 MB, resume at 512 KB.
        private static readonly PipeOptions UploadPipeOptions = new(
            pauseWriterThreshold: 1 * 1024 * 1024,
            resumeWriterThreshold: 512 * 1024,
            useSynchronizationContext: false);

        // Download pipe: allow Azure to buffer 4 MB ahead of the tiny 52-byte record reader.
        private static readonly PipeOptions DownloadPipeOptions = new(
            pauseWriterThreshold: 4 * 1024 * 1024,
            resumeWriterThreshold: 1 * 1024 * 1024,
            useSynchronizationContext: false);

        // ---------------------------------------------------------------------
        // Construction
        // ---------------------------------------------------------------------

        /// <param name="connectionString">Azure Storage connection string.</param>
        /// <param name="containerName">Blob container name.</param>
        /// <param name="prefix">Optional blob name prefix (e.g. "candles"). No leading/trailing slashes needed.</param>
        /// <param name="logger">Optional logger.</param>
        public AzureBlobCandlesRepository(string connectionString, string containerName, string? prefix = null, ILogger<AzureBlobCandlesRepository>? logger = null)
            : this(new BlobContainerClient(connectionString, containerName), prefix, logger) { }

        /// <param name="serviceClient">Pre-built <see cref="BlobServiceClient"/> authenticated with managed identity or other credentials.</param>
        /// <param name="containerName">Blob container name.</param>
        /// <param name="prefix">Optional blob name prefix (e.g. "candles"). No leading/trailing slashes needed.</param>
        /// <param name="logger">Optional logger.</param>
        public AzureBlobCandlesRepository(BlobServiceClient serviceClient, string containerName, string? prefix = null, ILogger<AzureBlobCandlesRepository>? logger = null)
            : this(serviceClient.GetBlobContainerClient(containerName), prefix, logger) { }

        /// <param name="container">Pre-built <see cref="BlobContainerClient"/> (useful for testing with Azurite or a fake).</param>
        /// <param name="prefix">Optional blob name prefix.</param>
        /// <param name="logger">Optional logger.</param>
        public AzureBlobCandlesRepository(BlobContainerClient container, string? prefix = null, ILogger<AzureBlobCandlesRepository>? logger = null)
            : base(prefix?.Trim('/') ?? string.Empty, (ILogger?)logger ?? NullLogger<AzureBlobCandlesRepository>.Instance)
        {
            _container = container;
            _prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim('/');
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private string BlobName(string fileName)
            => _prefix is null ? fileName : $"{_prefix}/{fileName}";

        private BlockBlobClient GetBlockBlobClient(string blobName)
            => _container.GetBlockBlobClient(blobName);

        // ------------------------------------------------------------------
        // CandlesRepositoryBase abstract members
        // ------------------------------------------------------------------

        protected override async IAsyncEnumerable<(string Path, long Size)> EnumerateFilesAsync()
        {
            var blobPrefix = _prefix is null ? null : _prefix + "/";
            await foreach (var item in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, blobPrefix, default))
            {
                if (item.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    yield return (item.Name, item.Properties.ContentLength ?? 0);
            }
        }

        protected override string GetFileName(string filePathOrKey)
        {
            if (string.IsNullOrEmpty(filePathOrKey)) return string.Empty;
            if (_prefix is not null && filePathOrKey.StartsWith(_prefix))
                filePathOrKey = filePathOrKey[_prefix.Length..].TrimStart('/');
            return filePathOrKey;
        }

        /// <summary>
        /// Returns the write end of a <see cref="Pipe"/>. A background task simultaneously
        /// reads from the pipe's read end and uploads the bytes directly to Azure Blob Storage
        /// under a temporary blob name. No local disk I/O occurs.
        /// </summary>
        protected override Task<Stream> OpenWriteStreamAsync(string tempPath)
        {
            var tempBlobName = _prefix is null
                ? $"_tmp/{Path.GetFileName(tempPath)}"
                : $"{_prefix}/_tmp/{Path.GetFileName(tempPath)}";

            var pipe = new Pipe(UploadPipeOptions);

            var uploadTask = Task.Run(async () =>
            {
                await using var readStream = pipe.Reader.AsStream();
                try
                {
                    var blob = GetBlockBlobClient(tempBlobName);
                    await blob.UploadAsync(readStream, new BlobUploadOptions());
                }
                catch (Exception ex)
                {
                    await pipe.Writer.CompleteAsync(ex);
                    return;
                }
                
            });

            _pendingUploads[tempPath] = (uploadTask, tempBlobName);
            return Task.FromResult<Stream>(pipe.Writer.AsStream());
        }

        /// <summary>
        /// Awaits the in-flight upload, then does a server-side blob copy to the final name
        /// and deletes the temp blob — no data re-transfer between client and Azure.
        /// </summary>
        protected override async Task MoveTempToFinalAsync(string tempPath, string finalPath)
        {
            if (!_pendingUploads.TryRemove(tempPath, out var pending))
                return;

            await pending.UploadTask;

            var finalBlobName = BlobName(Path.GetFileName(finalPath));
            var sourceBlob = GetBlockBlobClient(pending.TempBlobName);
            var destBlob = GetBlockBlobClient(finalBlobName);

            // Server-side copy
            var copyOp = await destBlob.StartCopyFromUriAsync(sourceBlob.Uri);
            await copyOp.WaitForCompletionAsync();

            await sourceBlob.DeleteIfExistsAsync();
        }

        /// <summary>
        /// Called by the base class when the candle stream is empty.
        /// Awaits and discards the upload task, then deletes the temp blob.
        /// </summary>
        protected override async Task TryDeleteTempAsync(string tempPath)
        {
            if (!_pendingUploads.TryRemove(tempPath, out var pending))
                return;
            try { await pending.UploadTask; } catch { }
            try { await GetBlockBlobClient(pending.TempBlobName).DeleteIfExistsAsync(); } catch { }
        }

        /// <summary>
        /// Returns a stream that reads directly from Azure Blob Storage.
        /// A background task downloads from Azure into the write end of a <see cref="Pipe"/>;
        /// the caller receives the read end wrapped in <see cref="BackgroundTaskStream"/>.<br/>
        /// Errors surface in two ways:
        /// <list type="bullet">
        ///   <item>During reads — the <see cref="Pipe"/> propagates the background exception to the next <c>ReadAsync</c>.</item>
        ///   <item>On dispose — <see cref="BackgroundTaskStream.DisposeAsync"/> awaits the background task and rethrows.</item>
        /// </list>
        /// </summary>
        protected override Task<Stream> OpenReadStreamAsync(string filePathOrKey, long offset)
        {
            var blobName = filePathOrKey;
            if (_prefix is not null && !filePathOrKey.StartsWith(_prefix))
                blobName = BlobName(GetFileName(filePathOrKey));

            var pipe = new Pipe(DownloadPipeOptions);            

            var downloadTask = Task.Run(async () =>
            {
                try
                {
                    await using var writeStream = pipe.Writer.AsStream();
                    var blob = GetBlockBlobClient(blobName);
                    var options = offset > 0
                        ? new BlobDownloadOptions { Range = new HttpRange(offset) }
                        : null;
                    var response = await blob.DownloadStreamingAsync(options);
                    await response.Value.Content.CopyToAsync(writeStream);
                }
                catch (Exception ex)
                {
                    await pipe.Writer.CompleteAsync(ex);
                    
                }
            });

            return Task.FromResult(pipe.Reader.AsStream());
        }

        // ------------------------------------------------------------------
        // Override: delete helpers
        // ------------------------------------------------------------------
        public override async Task RemoveCandleFilesAsync(string symbol, int intervalMinutes, CancellationToken cancellationToken = default)
        {
            var files = await GetCandleFilesAsync(symbol, intervalMinutes, cancellationToken);
            foreach (var file in files)
                await RemoveCandleFileAsync(file, cancellationToken);
        }

        public override async Task RemoveCandleFileAsync(CandleFileInfo fileInfo, CancellationToken cancellationToken = default)
        {
            var blobName = fileInfo.Path;
            if (_prefix is not null && !blobName.StartsWith(_prefix))
                blobName = BlobName(Path.GetFileName(fileInfo.Path));

            await _container.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: cancellationToken);

            foreach (var kvp in _fileIndex)
                kvp.Value.RemoveAll(f => f.ToPublic().Path == fileInfo.Path);
        }

        // ------------------------------------------------------------------
        // Override: job config from Azure Blob Storage
        // ------------------------------------------------------------------

        public override async Task<IReadOnlyList<PairJobConfig>> GetJobConfigAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var blob = _container.GetBlobClient(ConfigKey);
                var response = await blob.DownloadContentAsync(cancellationToken);
                var content = response.Value.Content.ToString();
                var lines = content.Split('\n', StringSplitOptions.None);
                return PairJobConfigReader.ParseLines(lines);
            }
            catch { return Array.Empty<PairJobConfig>(); }
        }
        
    }
}

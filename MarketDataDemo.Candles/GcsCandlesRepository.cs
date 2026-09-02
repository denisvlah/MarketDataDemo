using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Google.Api.Gax;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketDataDemo.Candles
{
    /// <summary>
    /// Google Cloud Storage backend for candle storage and retrieval.
    /// Streams candle data directly to GCS via a System.IO.Pipelines.Pipe:
    /// the write end is returned to the caller, while a background task feeds
    /// the read end into StorageClient.UploadObjectAsync — no local temp files.
    /// MoveTempToFinalAsync awaits the upload then does a server-side GCS copy
    /// to the final key, keeping the two-step base-class store pattern intact.
    /// </summary>
    public class GcsCandlesRepository : CandlesRepositoryBase
    {
        private readonly StorageClient _storageClient;
        private readonly string _bucket;
        private readonly string? _prefix;

        // Tracks in-flight GCS uploads keyed by the abstract temp path.
        // Entry is added in OpenWriteStreamAsync and consumed in MoveTempToFinalAsync / TryDeleteTempAsync.
        private readonly ConcurrentDictionary<string, (Task UploadTask, string TempGcsKey)> _pendingUploads = new();

        // --- Pipe sizing ---
        // Upload pipe: pause writer (base class) at 1 MB so the serialisation loop
        // never runs too far ahead of the GCS upload; resume at 512 KB.
        private static readonly PipeOptions UploadPipeOptions = new(
            pauseWriterThreshold: 1 * 1024 * 1024,
            resumeWriterThreshold: 512 * 1024,
            useSynchronizationContext: false);

        // Download pipe: allow GCS to buffer up to 4 MB ahead of the candle reader
        // (each 52-byte record is tiny; a larger window keeps the TCP stream full);
        // resume pumping once the reader has drained to 1 MB.
        private static readonly PipeOptions DownloadPipeOptions = new(
            pauseWriterThreshold: 4 * 1024 * 1024,
            resumeWriterThreshold: 1 * 1024 * 1024,
            useSynchronizationContext: false);

        // ---------------------------------------------------------------------
        // Construction
        // ---------------------------------------------------------------------

        /// <param name="bucket">GCS bucket name.</param>
        /// <param name="prefix">Optional object-key prefix (e.g. "candles"). No leading/trailing slashes needed.</param>
        /// <param name="client">Optional pre-built StorageClient (useful for testing with a fake/emulator).</param>
        public GcsCandlesRepository(string bucket, string? prefix = null, StorageClient? client = null, ILogger<GcsCandlesRepository>? logger = null)
            : base(prefix?.Trim('/') ?? string.Empty, (ILogger?)logger ?? NullLogger<GcsCandlesRepository>.Instance)
        {
            _bucket = bucket;
            _prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim('/');
            _storageClient = client ?? StorageClient.Create();
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private string ObjectName(string fileName)
            => _prefix is null ? fileName : $"{_prefix}/{fileName}";

        // ------------------------------------------------------------------
        // CandlesRepositoryBase abstract members
        // ------------------------------------------------------------------

        protected override async IAsyncEnumerable<(string Path, long Size)> EnumerateFilesAsync()
        {
            var prefix = _prefix is null ? null : _prefix + "/";
            var options = new ListObjectsOptions { Projection = Projection.NoAcl };
            await foreach (var obj in _storageClient.ListObjectsAsync(_bucket, prefix, options))
            {
                if (obj.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    yield return (obj.Name, (long)(obj.Size ?? 0));
            }
        }

        protected override string GetFileName(string filePathOrKey)
        {
            if (string.IsNullOrEmpty(filePathOrKey)) return string.Empty;
            // Strip the prefix so the base-class regex sees just the bare filename.
            if (_prefix is not null && filePathOrKey.StartsWith(_prefix))
                filePathOrKey = filePathOrKey[_prefix.Length..].TrimStart('/');
            return filePathOrKey;
        }

        /// <summary>
        /// Returns the write end of a <see cref="Pipe"/>. A background task simultaneously
        /// reads from the pipe's read end and streams the bytes directly to GCS under a
        /// temporary object key. No local disk I/O occurs.
        /// </summary>
        protected override Task<Stream> OpenWriteStreamAsync(string tempPath)
        {
            // Temporary GCS key lives under the same prefix so it is easy to clean up.
            var tempGcsKey = _prefix is null
                ? $"_tmp/{Path.GetFileName(tempPath)}"
                : $"{_prefix}/_tmp/{Path.GetFileName(tempPath)}";

            var pipe = new Pipe(UploadPipeOptions);

            // Upload runs concurrently; the caller writes into pipe.Writer, GCS reads from pipe.Reader.
            var uploadTask = Task.Run(async () =>
            {
                await using var readStream = pipe.Reader.AsStream();
                await _storageClient.UploadObjectAsync(
                    _bucket, tempGcsKey, "application/octet-stream", readStream);
            });

            _pendingUploads[tempPath] = (uploadTask, tempGcsKey);
            return Task.FromResult<Stream>(pipe.Writer.AsStream());
        }

        /// <summary>
        /// Awaits the in-flight GCS upload started by <see cref="OpenWriteStreamAsync"/>,
        /// then server-side copies the temp object to its final key and deletes the temp.
        /// </summary>
        protected override async Task MoveTempToFinalAsync(string tempPath, string finalPath)
        {
            if (!_pendingUploads.TryRemove(tempPath, out var pending))
                return;

            await pending.UploadTask; // wait for direct-stream upload to finish

            var finalKey = ObjectName(Path.GetFileName(finalPath));
            // Server-side copy: no data re-transfer between client and GCS.
            await _storageClient.CopyObjectAsync(_bucket, pending.TempGcsKey, _bucket, finalKey);
            await _storageClient.DeleteObjectAsync(_bucket, pending.TempGcsKey);
        }

        /// <summary>
        /// Called by the base class when the candle stream is empty. Awaits and discards
        /// the upload task, then deletes the (empty) GCS temp object.
        /// </summary>
        protected override async Task TryDeleteTempAsync(string tempPath)
        {
            if (!_pendingUploads.TryRemove(tempPath, out var pending))
                return;
            try { await pending.UploadTask; } catch { }
            try { await _storageClient.DeleteObjectAsync(_bucket, pending.TempGcsKey); } catch { }
        }

        /// <summary>
        /// Returns a stream that reads directly from GCS without buffering in memory.
        /// A background task downloads from GCS into the write end of a <see cref="Pipe"/>;
        /// the caller receives the read end wrapped in <see cref="BackgroundTaskStream"/>.<br/>
        /// Errors surface in two ways:
        /// <list type="bullet">
        ///   <item>During reads — <see cref="Pipe"/> propagates the background exception to the next <c>ReadAsync</c>.</item>
        ///   <item>On dispose — <see cref="BackgroundTaskStream.DisposeAsync"/> awaits the background task
        ///         and rethrows, so the <c>await using</c> in <c>FetchCandlesAsync</c> always sees the error.</item>
        /// </list>
        /// </summary>
        protected override Task<Stream> OpenReadStreamAsync(string filePathOrKey, long offset)
        {
            var objectName = filePathOrKey;
            if (_prefix is not null && !filePathOrKey.StartsWith(_prefix))
                objectName = ObjectName(GetFileName(filePathOrKey));

            var options = offset > 0
                ? new DownloadObjectOptions { Range = new RangeHeaderValue(offset, null) }
                : null;

            var pipe = new Pipe(DownloadPipeOptions);

            var downloadTask = Task.Run(async () =>
            {
                try
                {
                    await using var writeStream = pipe.Writer.AsStream();
                    await _storageClient.DownloadObjectAsync(_bucket, objectName, writeStream, options);
                    // writeStream.DisposeAsync completes the pipe writer normally.
                }
                catch (Exception ex)
                {
                    // Fault the pipe so the next ReadAsync on the reader throws immediately.
                    await pipe.Writer.CompleteAsync(ex);
                    throw; // preserved in downloadTask so DisposeAsync can rethrow it.
                }
            });

            // Wrap the reader stream with the background task so dispose always awaits + rethrows.
            Stream result = new BackgroundTaskStream(pipe.Reader.AsStream(), downloadTask);
            return Task.FromResult(result);
        }

        /// <summary>
        /// Wraps a stream and awaits a background <see cref="Task"/> on disposal,
        /// rethrowing any exception from the task so callers never silently lose errors.
        /// </summary>
        /// TODO: make better exception rethrowing.
        private sealed class BackgroundTaskStream(Stream inner, Task backgroundTask) : Stream
        {
            public override bool CanRead  => inner.CanRead;
            public override bool CanSeek  => false;
            public override bool CanWrite => false;
            public override long Length   => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int  Read(byte[] buffer, int offset, int count)              => inner.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)   => inner.ReadAsync(buffer, ct);
            public override void Flush()                                                 => inner.Flush();
            public override long Seek(long offset, SeekOrigin origin)                   => throw new NotSupportedException();
            public override void SetLength(long value)                                  => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count)            => throw new NotSupportedException();

            public override async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                await backgroundTask; // rethrows if the download faulted
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                // Synchronous dispose cannot await; errors will surface via DisposeAsync.
                base.Dispose(disposing);
            }
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
            var objectName = fileInfo.Path;
            if (_prefix is not null && !objectName.StartsWith(_prefix))
                objectName = ObjectName(Path.GetFileName(fileInfo.Path));

            await _storageClient.DeleteObjectAsync(_bucket, objectName, cancellationToken: cancellationToken);

            foreach (var kvp in _fileIndex)
                kvp.Value.RemoveAll(f => f.Path == fileInfo.Path);
        }

        // ------------------------------------------------------------------
        // Override: job config from GCS
        // ------------------------------------------------------------------

        public override async Task<IReadOnlyList<PairJobConfig>> GetJobConfigAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var objectName = ConfigKey; // "config/kraken-collector-config.csv"
                var stream = new MemoryStream();
                await _storageClient.DownloadObjectAsync(_bucket, objectName, stream, cancellationToken: cancellationToken);
                stream.Position = 0;
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync(cancellationToken);
                var lines = content.Split('\n', StringSplitOptions.None);
                return PairJobConfigReader.ParseLines(lines);
            }
            catch { return Array.Empty<PairJobConfig>(); }
        }

    }
}

using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketDataDemo.Candles
{
    public class FileSystemCandlesRepository : CandlesRepositoryBase
    {
        public FileSystemCandlesRepository(string baseDirectory, ILogger<FileSystemCandlesRepository>? logger = null)
            : base(baseDirectory, (ILogger?)logger ?? NullLogger<FileSystemCandlesRepository>.Instance)
        {
        }

        // Inherits GetCandleFilesAsync, RemoveCandleFilesAsync, RemoveCandleFileAsync from base

        protected override async IAsyncEnumerable<(string Path, long Size)> EnumerateFilesAsync()
        {
            foreach (var file in Directory.EnumerateFiles(_baseLocation, "*.bin"))
                yield return (file, new FileInfo(file).Length);
            await Task.CompletedTask;
        }

        protected override string GetFileName(string filePathOrKey)
        {
            return Path.GetFileName(filePathOrKey);
        }

        protected override Task<Stream> OpenWriteStreamAsync(string tempPath)
        {
            Stream s = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536);
            return Task.FromResult(s);
        }

        protected override Task MoveTempToFinalAsync(string tempPath, string finalPath)
        {
            File.Move(tempPath, finalPath);
            return Task.CompletedTask;
        }

        protected override Task<Stream> OpenReadStreamAsync(string filePathOrKey, long offset)
        {
            Stream s = new FileStream(filePathOrKey, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, FileOptions.SequentialScan);
            s.Seek(offset, SeekOrigin.Begin);
            return Task.FromResult(s);
        }

        public override Task<IReadOnlyList<PairJobConfig>> GetJobConfigAsync(CancellationToken cancellationToken = default)
        {
            var filePath = Path.Combine(_baseLocation, "config", "kraken-collector-config.csv");
            if (!File.Exists(filePath))
                return Task.FromResult<IReadOnlyList<PairJobConfig>>(Array.Empty<PairJobConfig>());
            return Task.FromResult<IReadOnlyList<PairJobConfig>>(PairJobConfigReader.ReadFromFile(filePath));
        }
    }
}

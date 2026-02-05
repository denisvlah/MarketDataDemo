using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace S3CandlesDemo.Candles
{
    public class FileSystemCandlesRepository : CandlesRepositoryBase
    {
        public FileSystemCandlesRepository(string baseDirectory) : base(baseDirectory)
        {
        }

        // Inherits GetCandleFilesAsync, RemoveCandleFilesAsync, RemoveCandleFileAsync from base

        protected override IEnumerable<string> EnumerateFiles()
        {
            return Directory.EnumerateFiles(_baseLocation, "*.bin");
        }

        protected override string GetFileName(string filePathOrKey)
        {
            return Path.GetFileName(filePathOrKey);
        }

        protected override Task<Stream> OpenWriteStreamAsync(string tempPath)
        {
            Stream s = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            return Task.FromResult(s);
        }

        protected override Task MoveTempToFinalAsync(string tempPath, string finalPath)
        {
            File.Move(tempPath, finalPath);
            return Task.CompletedTask;
        }

        protected override Task<Stream> OpenReadStreamAsync(string filePathOrKey, long offset)
        {
            Stream s = new FileStream(filePathOrKey, FileMode.Open, FileAccess.Read, FileShare.Read);
            s.Seek(offset, SeekOrigin.Begin);
            return Task.FromResult(s);
        }
    }
}

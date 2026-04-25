using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace S3CandlesDemo.Candles
{
    public abstract partial class CandlesRepositoryBase : ICandlesRepository
    {
        protected readonly string _baseLocation;
        
        // TODO: use here the sorted list that is sorted by start time ASC and verson DESC.
        protected readonly ConcurrentDictionary<(string symbol, int interval), List<CandleFileInfoInternal>> _fileIndex = new();
        private static readonly Regex FilePattern = FilePatternRegex();

        [GeneratedRegex("^(?<symbol>[^_]+)_(?<interval>\\d+)_(?<start>\\d{8}T\\d{6})_(?<end>\\d{8}T\\d{6})_v(?<version>\\d+)\\.bin$")]
        private static partial Regex FilePatternRegex();

        protected CandlesRepositoryBase(string baseLocation)
        {
            _baseLocation = baseLocation;
        }

        protected async Task BuildFileIndexAsync()
        {            
            var keys = new HashSet<(string symbol, int interval)>();
            await foreach (var file in EnumerateFilesAsync())
            {
                var name = GetFileName(file);
                var match = FilePattern.Match(name);
                if (!match.Success) continue;
                var symbol = match.Groups["symbol"].Value;
                var interval = int.Parse(match.Groups["interval"].Value);
                var start = DateTime.ParseExact(match.Groups["start"].Value, "yyyyMMdd'T'HHmmss", null);
                var end = DateTime.ParseExact(match.Groups["end"].Value, "yyyyMMdd'T'HHmmss", null);
                var version = int.Parse(match.Groups["version"].Value);
                var info = new CandleFileInfoInternal(file, start, end, version);
                var key = (symbol, interval);
                keys.Add(key);
                _fileIndex.AddOrUpdate(key, k => new List<CandleFileInfoInternal> { info }, (k, list) => { list.Add(info); return list; });
            }
            foreach( var kvp in _fileIndex)
            {
                if (!keys.Contains(kvp.Key))
                    _fileIndex.TryRemove(kvp.Key, out _);
            }
            
            foreach (var kvp in _fileIndex)
                kvp.Value.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        public Task RebuildFileIndexAsync(CancellationToken cancellationToken = default) => BuildFileIndexAsync();

        protected abstract IAsyncEnumerable<string> EnumerateFilesAsync();
        protected abstract string GetFileName(string filePathOrKey);
        protected abstract Task<Stream> OpenWriteStreamAsync(string tempPath);
        protected abstract Task MoveTempToFinalAsync(string tempPath, string finalPath);
        protected abstract Task<Stream> OpenReadStreamAsync(string filePathOrKey, long offset);

        public async Task StoreCandlesAsync(string symbol, int intervalMinutes, IEnumerable<Candle> candles, CancellationToken cancellationToken = default)
        {
            await StoreCandlesAsync(symbol, intervalMinutes, ToAsyncEnumerable(candles), cancellationToken);
        }

        public virtual async Task StoreCandlesAsync(string symbol, int intervalMinutes, IAsyncEnumerable<Candle> candles, CancellationToken cancellationToken = default)
        {
            string tempFileName = $"{symbol}_{intervalMinutes}_{Guid.NewGuid()}.tmp";
            string tempFilePath = PathCombine(_baseLocation, tempFileName);
            DateTime? minTimestamp = null, maxTimestamp = null;
            byte[] buffer = new byte[Candle.CandleByteSize];
            await using (var stream = await OpenWriteStreamAsync(tempFilePath))
            {
                await foreach (var candle in candles.WithCancellation(cancellationToken))
                {
                    Candle.CandleToBytes(candle, buffer);
                    await stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (!minTimestamp.HasValue || candle.Timestamp < minTimestamp.Value)
                        minTimestamp = candle.Timestamp;
                    if (!maxTimestamp.HasValue || candle.Timestamp > maxTimestamp.Value)
                        maxTimestamp = candle.Timestamp;
                }
            }
            if (!minTimestamp.HasValue || !maxTimestamp.HasValue)
            {
                await TryDeleteTempAsync(tempFilePath);
                return;
            }
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
            string newFilePath = PathCombine(_baseLocation, newFileName);
            await MoveTempToFinalAsync(tempFilePath, newFilePath);
            var info = new CandleFileInfoInternal(newFilePath, start, end, newVersion);
            _fileIndex.AddOrUpdate(key, k => new List<CandleFileInfoInternal> { info }, (k, list) => { list.Add(info); list.Sort((a, b) => a.Start.CompareTo(b.Start)); return list; });
        }

        public async IAsyncEnumerable<Candle> FetchCandlesAsync(string symbol, int intervalMinutes, DateTime from, DateTime to, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var key = (symbol, intervalMinutes);
            if (!_fileIndex.TryGetValue(key, out var files)) yield break;
            files = files.Where(f => f.End >= from && f.Start <= to).OrderBy(f => f.Start).ThenByDescending(x => x.Version).ToList();

            var buffer = new byte[Candle.CandleByteSize];
            var lastCandleTimestamp = from;
            foreach (var file in files)
            {
                if (lastCandleTimestamp > file.End)
                    continue;
                var offset = 0L;
                if (lastCandleTimestamp > file.Start) offset = (long)((lastCandleTimestamp - file.Start).TotalMinutes / intervalMinutes) * Candle.CandleByteSize;
                await using var stream = await OpenReadStreamAsync(file.Path, offset);
                if (stream == null) continue;
                while (true)
                {
                    int totalRead = 0;
                    while (totalRead < Candle.CandleByteSize)
                    {
                        int read = await stream.ReadAsync(buffer, totalRead, Candle.CandleByteSize - totalRead, cancellationToken);
                        if (read == 0) break;
                        totalRead += read;
                    }
                    if (totalRead != Candle.CandleByteSize) break;
                    var candle = Candle.BytesToCandle(buffer);
                    if (candle.Timestamp > to) break;
                    lastCandleTimestamp = candle.Timestamp;
                    yield return candle;
                }
            }
        }

        private static async IAsyncEnumerable<Candle> ToAsyncEnumerable(IEnumerable<Candle> candles)
        {
            foreach (var candle in candles)
                yield return candle;
            await Task.CompletedTask;
        }

        protected virtual string PathCombine(string a, string b) => Path.Combine(a, b);
        protected virtual Task TryDeleteTempAsync(string tempPath)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return Task.CompletedTask;
        }

        // Internal version for storage, maps to public CandleFileInfo
        protected class CandleFileInfoInternal
        {
            public string Path { get; }
            public DateTime Start { get; }
            public DateTime End { get; }
            public int Version { get; }

            public CandleFileInfoInternal(string path, DateTime start, DateTime end, int version)
            {
                Path = path;
                Start = start;
                End = end;
                Version = version;
            }
            public S3CandlesDemo.Candles.CandleFileInfo ToPublic() => new S3CandlesDemo.Candles.CandleFileInfo { Path = Path, Start = Start, End = End, Version = Version };
        }

        // ICandlesRepository additions
        public virtual Task<IReadOnlyList<CandleFileInfo>> GetCandleFilesAsync(string symbol, int intervalMinutes, CancellationToken cancellationToken = default)
        {
            var key = (symbol, intervalMinutes);
            if (!_fileIndex.TryGetValue(key, out var files))
                return Task.FromResult((IReadOnlyList<CandleFileInfo>)Array.Empty<CandleFileInfo>());
            return Task.FromResult((IReadOnlyList<CandleFileInfo>)files.Select(f => f.ToPublic()).ToList());
        }

        public virtual async Task RemoveCandleFilesAsync(string symbol, int intervalMinutes, CancellationToken cancellationToken = default)
        {
            var key = (symbol, intervalMinutes);
            if (_fileIndex.TryRemove(key, out var files))
                foreach (var file in files)
                    await RemovePhysicalFile(file.Path);

            return;
        }

        public virtual async Task RemoveCandleFileAsync(CandleFileInfo fileInfo, CancellationToken cancellationToken = default)
        {
            await RemovePhysicalFile(fileInfo.Path);
            foreach (var kvp in _fileIndex) kvp.Value.RemoveAll(f => f.Path == fileInfo.Path);
        }

        public virtual async Task<IReadOnlyList<CandleFileInfoDetail>> GetAllCandleFilesAsync(CancellationToken cancellationToken = default)
        {
            var result = new List<CandleFileInfoDetail>();
            foreach (var kvp in _fileIndex)
            {
                var (symbol, interval) = kvp.Key;
                foreach (var file in kvp.Value)
                {
                    long fileSize = await GetFileSizeAsync(file.Path);
                    long candleCount = fileSize / Candle.CandleByteSize;
                    result.Add(new CandleFileInfoDetail
                    {
                        Symbol = symbol,
                        IntervalMinutes = interval,
                        Path = file.Path,
                        Start = file.Start,
                        End = file.End,
                        Version = file.Version,
                        FileSize = fileSize,
                        CandleCount = candleCount
                    });
                }
            }
            return (IReadOnlyList<CandleFileInfoDetail>)result;
        }

        protected virtual Task<long> GetFileSizeAsync(string path)
        {
            try { return Task.FromResult(new FileInfo(path).Length); } catch { return Task.FromResult(0L); }
        }

        protected virtual Task RemovePhysicalFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
            return Task.CompletedTask;
        }

        public List<(DateTime Start, DateTime End)> GetGaps(string symbol, int intervalMinutes, DateTime minDate)
        {
            var key = (symbol, intervalMinutes);
            if (!_fileIndex.TryGetValue(key, out var files))
                return new List<(DateTime Start, DateTime End)> { (minDate, DateTime.MaxValue) };

            var gaps = new List<(DateTime Start, DateTime End)>();
            DateTime current = minDate;
            foreach (var file in files.OrderBy(f => f.Start))
            {
                if (file.End < current) continue; // file is completely before current
                if (file.Start > current)
                    gaps.Add((current, file.Start)); // gap between current and start of file
                current = file.End > current ? file.End : current; // move current forward
            }
            if (current < DateTime.MaxValue)
                gaps.Add((current, DateTime.MaxValue)); // gap from end of last file to infinity
            return gaps;
        }
    }
}

namespace S3CandlesDemo.Candles
{

    // Dummy implementation for API wiring
    public class DummyCandlesRepository : ICandlesRepository
    {
        public Task StoreCandlesAsync(string symbol, int intervalMinutes, IEnumerable<Candle> candles, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task StoreCandlesAsync(string symbol, int intervalMinutes, IAsyncEnumerable<Candle> candles, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public async IAsyncEnumerable<Candle> FetchCandlesAsync(string symbol, int intervalMinutes, DateTime from, DateTime to, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
        public Task<IReadOnlyList<CandleFileInfo>> GetCandleFilesAsync(string symbol, int intervalMinutes, CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<CandleFileInfo>)Array.Empty<CandleFileInfo>());

        public Task RemoveCandleFilesAsync(string symbol, int intervalMinutes, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveCandleFileAsync(CandleFileInfo fileInfo, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CandleFileInfoDetail>> GetAllCandleFilesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<CandleFileInfoDetail>)Array.Empty<CandleFileInfoDetail>());

        public Task RebuildFileIndexAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<PairJobConfig>> GetJobConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PairJobConfig>>(Array.Empty<PairJobConfig>());

        public List<(DateTime Start, DateTime End)> GetGaps(string symbol, int intervalMinutes, DateTime minDate)
        {
            return new List<(DateTime Start, DateTime End)>();
        }
    }
}

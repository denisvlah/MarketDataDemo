namespace S3CandlesDemo.Candles
{
    public interface ICandlesRepository
    {
        // Store candles (sorted)
        Task StoreCandlesAsync(string symbol, int intervalMinutes, IEnumerable<Candle> candles, CancellationToken cancellationToken = default);
        Task StoreCandlesAsync(string symbol, int intervalMinutes, IAsyncEnumerable<Candle> candles, CancellationToken cancellationToken = default);

        // Fetch candles by symbol, time period, and interval
        IAsyncEnumerable<Candle> FetchCandlesAsync(string symbol, int intervalMinutes, DateTime from, DateTime to, CancellationToken cancellationToken = default);

        // Retrieve all file info for a symbol/interval
        Task<IReadOnlyList<CandleFileInfo>> GetCandleFilesAsync(string symbol, int intervalMinutes, CancellationToken cancellationToken = default);

        // Remove all files for a symbol/interval
        Task RemoveCandleFilesAsync(string symbol, int intervalMinutes, CancellationToken cancellationToken = default);

        // Remove a specific file by file info
        Task RemoveCandleFileAsync(CandleFileInfo fileInfo, CancellationToken cancellationToken = default);

        // List all files across all symbols/intervals
        Task<IReadOnlyList<CandleFileInfoDetail>> GetAllCandleFilesAsync(CancellationToken cancellationToken = default);

        // Rebuild the in-memory file index from the underlying storage
        Task RebuildFileIndexAsync(CancellationToken cancellationToken = default);

        // Read the job config (pair/interval pairs) from the underlying storage
        Task<IReadOnlyList<PairJobConfig>> GetJobConfigAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Calculate gaps in the stored candles for a given symbol and interval, starting from a minimum date. This can be used to identify missing data ranges that need to be backfilled.
        /// </summary>
        /// <param name="symbol">The symbol for which to calculate gaps.</param>
        /// <param name="intervalMinutes">The interval in minutes for the candles.</param>
        /// <param name="minDate">The minimum date from which to start calculating gaps.</param>
        /// <returns>A list of tuples representing the start and end of each gap.</returns>
        List<(DateTime Start, DateTime End)> GetGaps(string symbol, int intervalMinutes, DateTime minDate);
    }

    // Expose CandleFileInfo for consumers
    public class CandleFileInfo
    {
        public string Path { get; set; } = string.Empty;
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public int Version { get; set; }
    }

    public class CandleFileInfoDetail
    {
        public string Symbol { get; set; } = string.Empty;
        public int IntervalMinutes { get; set; }
        public string Path { get; set; } = string.Empty;
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public int Version { get; set; }
        public long FileSize { get; set; }
        public long CandleCount { get; set; }
    }
    
}

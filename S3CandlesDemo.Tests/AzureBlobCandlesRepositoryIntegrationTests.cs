using Azure.Storage.Blobs;
using S3CandlesDemo.Candles;

namespace S3CandlesDemo.Tests;

/// <summary>
/// Integration tests for <see cref="AzureBlobCandlesRepository"/> that write/read data
/// directly against a real Azure Blob Storage account.
///
/// Prerequisites:
///   Set the following environment variables (or put them in the .env file in the repo root
///   and source it before running):
///     AZURE_SOTRAGE_CONNECTION_STRING  — Azure Storage connection string
///     AZURE_STORAGE_CONTAINER          — Blob container name
///
/// Each test creates blobs, asserts behaviour, then cleans up after itself.
/// </summary>
public class AzureBlobCandlesRepositoryIntegrationTests : IAsyncLifetime
{
    private const string ConnectionStringEnvVar = "AZURE_SOTRAGE_CONNECTION_STRING";  // note: intentional typo matches .env file
    private const string ContainerEnvVar = "AZURE_STORAGE_CONTAINER";

    private BlobContainerClient _container = null!;
    private string _prefix = null!;
    private AzureBlobCandlesRepository _repo = null!;

    // Loaded once per test class
    private static readonly string? ConnectionString = GetEnv(ConnectionStringEnvVar);
    private static readonly string? ContainerName    = GetEnv(ContainerEnvVar);

    private static string? GetEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) return value;

        // Walk up from the current directory (max 15 levels) searching for a .env file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 15; i++)
        {
            if (dir is null) break;
            var envFile = Path.Combine(dir.FullName, ".env");
            if (File.Exists(envFile))
            {
                var lines = File.ReadAllLines(envFile);
                foreach (var line in lines)
                {
                    if (line.StartsWith(name + "=", StringComparison.Ordinal))
                    {
                        var result = line[(name.Length + 1)..].Trim();
                        Console.WriteLine($"[GetEnv] Found '{name}' in '{envFile}' with value length: {result.Length}");
                        return result;
                    }
                }
                // .env found but key not present — stop searching.
                break;
            }
            dir = dir.Parent;
        }

        Console.WriteLine($"[GetEnv] Could not find '{name}'");
        return null;
    }

    private static void SkipIfNotConfigured()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString) || string.IsNullOrWhiteSpace(ContainerName))
            throw new SkipException(
                $"Skipping Azure integration tests: set {ConnectionStringEnvVar} and {ContainerEnvVar} env vars.");
    }

    public async Task InitializeAsync()
    {
        SkipIfNotConfigured();

        // Each test run gets its own prefix so parallel runs don't interfere.
        _prefix = $"test-{Guid.NewGuid():N}";
        _container = new BlobContainerClient(ConnectionString, ContainerName);
        await _container.CreateIfNotExistsAsync();
        _repo = new AzureBlobCandlesRepository(_container, prefix: _prefix);
        // Build the in-memory index (empty on start)
        await _repo.RebuildFileIndexAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is null) return;
        // Delete only the blobs written by this test run (under our unique prefix)
        await foreach (var blob in _container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, _prefix + "/", default))
            await _container.GetBlobClient(blob.Name).DeleteIfExistsAsync();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static List<Candle> MakeCandles(DateTime start, int intervalMinutes, int count) =>
        Enumerable.Range(0, count).Select(i => new Candle
        {
            Timestamp  = start.AddMinutes(i * intervalMinutes),
            Open       = 100.0 + i * 0.1,
            High       = 101.0 + i * 0.1,
            Low        = 99.0  + i * 0.1,
            Close      = 100.5 + i * 0.1,
            Volume     = 1000.0 + i,
            TradeCount = i
        }).ToList();

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task StoreAndFetch_AllCandles_RoundTrip()
    {
        const string symbol = "AZBTC";
        const int interval  = 5;
        var start   = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeCandles(start, interval, 20);

        await _repo.StoreCandlesAsync(symbol, interval, candles);

        var fetched = new List<Candle>();
        await foreach (var c in _repo.FetchCandlesAsync(symbol, interval, candles.First().Timestamp, candles.Last().Timestamp))
            fetched.Add(c);

        Assert.Equal(candles.Count, fetched.Count);
        for (int i = 0; i < candles.Count; i++)
        {
            Assert.Equal(candles[i].Timestamp,  fetched[i].Timestamp);
            Assert.Equal(candles[i].Open,        fetched[i].Open);
            Assert.Equal(candles[i].High,        fetched[i].High);
            Assert.Equal(candles[i].Low,         fetched[i].Low);
            Assert.Equal(candles[i].Close,       fetched[i].Close);
            Assert.Equal(candles[i].Volume,      fetched[i].Volume);
            Assert.Equal(candles[i].TradeCount,  fetched[i].TradeCount);
        }
    }

    [Fact]
    public async Task FetchCandles_FromMiddleOfFile_ReturnsSubset()
    {
        const string symbol = "AZETH";
        const int interval  = 10;
        var start   = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeCandles(start, interval, 20);

        await _repo.StoreCandlesAsync(symbol, interval, candles);

        var from = candles[8].Timestamp;
        var to   = candles[15].Timestamp;

        var fetched = new List<Candle>();
        await foreach (var c in _repo.FetchCandlesAsync(symbol, interval, from, to))
            fetched.Add(c);

        Assert.Equal(8, fetched.Count);
        Assert.Equal(from,                 fetched.First().Timestamp);
        Assert.Equal(candles[15].Timestamp, fetched.Last().Timestamp);
    }

    [Fact]
    public async Task StoreTwoBatches_FetchSubsetAcrossBoundary_ReturnsCorrectCandles()
    {
        const string symbol = "AZSOL";
        const int interval  = 5;
        var start = new DateTime(2024, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        var firstBatch  = MakeCandles(start, interval, 10);
        var secondBatch = MakeCandles(start.AddMinutes(10 * interval), interval, 10);

        await _repo.StoreCandlesAsync(symbol, interval, firstBatch);
        await _repo.StoreCandlesAsync(symbol, interval, secondBatch);

        var from = firstBatch[7].Timestamp;
        var to   = secondBatch[4].Timestamp;

        var fetched = new List<Candle>();
        await foreach (var c in _repo.FetchCandlesAsync(symbol, interval, from, to))
            fetched.Add(c);

        // Candles at index 7,8,9 from first batch + 0,1,2,3,4 from second = 8 total
        Assert.Equal(8, fetched.Count);
        Assert.Equal(from, fetched.First().Timestamp);
        Assert.Equal(to,   fetched.Last().Timestamp);
    }

    [Fact]
    public async Task GetCandleFiles_AfterStore_ReturnsFileInfo()
    {
        const string symbol = "AZADA";
        const int interval  = 15;
        var start   = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeCandles(start, interval, 5);

        await _repo.StoreCandlesAsync(symbol, interval, candles);
        var files = await _repo.GetCandleFilesAsync(symbol, interval);

        Assert.Single(files);
        Assert.Equal(candles.First().Timestamp, files[0].Start);
        Assert.Equal(candles.Last().Timestamp,  files[0].End);
    }

    [Fact]
    public async Task RemoveCandleFile_RemovesSingleFile()
    {
        const string symbol = "AZXRP";
        const int interval  = 5;
        var start   = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeCandles(start, interval, 5);

        await _repo.StoreCandlesAsync(symbol, interval, candles);
        var files = await _repo.GetCandleFilesAsync(symbol, interval);
        Assert.NotEmpty(files);

        await _repo.RemoveCandleFileAsync(files[0]);
        var filesAfter = await _repo.GetCandleFilesAsync(symbol, interval);
        Assert.Empty(filesAfter);
    }

    [Fact]
    public async Task RemoveCandleFiles_RemovesAll()
    {
        const string symbol = "AZBNB";
        const int interval  = 5;
        var start   = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        await _repo.StoreCandlesAsync(symbol, interval, MakeCandles(start, interval, 5));
        await _repo.StoreCandlesAsync(symbol, interval, MakeCandles(start.AddDays(1), interval, 5));

        var files = await _repo.GetCandleFilesAsync(symbol, interval);
        Assert.NotEmpty(files);

        await _repo.RemoveCandleFilesAsync(symbol, interval);
        var filesAfter = await _repo.GetCandleFilesAsync(symbol, interval);
        Assert.Empty(filesAfter);
    }

    [Fact]
    public async Task GetAllCandleFiles_ReturnsFilesWithSizeAndCandleCount()
    {
        const string symbol = "AZLTC";
        const int interval  = 30;
        var start   = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeCandles(start, interval, 10);

        await _repo.StoreCandlesAsync(symbol, interval, candles);
        var allFiles = await _repo.GetAllCandleFilesAsync();

        var ours = allFiles.Where(f => f.Symbol == symbol && f.IntervalMinutes == interval).ToList();
        Assert.Single(ours);
        Assert.Equal(10, ours[0].CandleCount);
        Assert.Equal(10L * Candle.CandleByteSize, ours[0].FileSize);
    }

    [Fact]
    public async Task StoreAndFetch_BTC_USD_RoundTrip()
    {
        const string symbol = "BTC/USD";
        const int interval  = 1;
        var start   = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeCandles(start, interval, 60);

        await _repo.StoreCandlesAsync(symbol, interval, candles);

        var fetched = new List<Candle>();
        await foreach (var c in _repo.FetchCandlesAsync(symbol, interval, candles.First().Timestamp, candles.Last().Timestamp))
            fetched.Add(c);

        Assert.Equal(candles.Count, fetched.Count);
        for (int i = 0; i < candles.Count; i++)
        {
            Assert.Equal(candles[i].Timestamp,  fetched[i].Timestamp);
            Assert.Equal(candles[i].Open,        fetched[i].Open);
            Assert.Equal(candles[i].Close,       fetched[i].Close);
            Assert.Equal(candles[i].TradeCount,  fetched[i].TradeCount);
        }
    }

    [Fact]
    public async Task StoreTwoBatches_BTC_USD_FetchSpanningBatches()
    {
        const string symbol = "BTC/USD";
        const int interval  = 15;
        var start = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        var firstBatch  = MakeCandles(start, interval, 12);
        var secondBatch = MakeCandles(start.AddMinutes(12 * interval), interval, 12);

        await _repo.StoreCandlesAsync(symbol, interval, firstBatch);
        await _repo.StoreCandlesAsync(symbol, interval, secondBatch);

        var from = firstBatch[9].Timestamp;
        var to   = secondBatch[5].Timestamp;

        var fetched = new List<Candle>();
        await foreach (var c in _repo.FetchCandlesAsync(symbol, interval, from, to))
            fetched.Add(c);

        // Candles at index 9,10,11 from first batch + 0,1,2,3,4,5 from second = 9 total
        Assert.Equal(9, fetched.Count);
        Assert.Equal(from, fetched.First().Timestamp);
        Assert.Equal(to,   fetched.Last().Timestamp);
    }

    [Fact]
    public async Task GetCandleFiles_BTC_USD_MultipleIntervals()
    {
        const string symbol = "BTC/USD";
        var start = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        // Store candles at multiple intervals
        await _repo.StoreCandlesAsync(symbol, 1, MakeCandles(start, 1, 10));
        await _repo.StoreCandlesAsync(symbol, 5, MakeCandles(start, 5, 10));
        await _repo.StoreCandlesAsync(symbol, 60, MakeCandles(start, 60, 10));

        var files1m   = await _repo.GetCandleFilesAsync(symbol, 1);
        var files5m   = await _repo.GetCandleFilesAsync(symbol, 5);
        var files60m  = await _repo.GetCandleFilesAsync(symbol, 60);

        Assert.Single(files1m);
        Assert.Single(files5m);
        Assert.Single(files60m);
    }

    [Fact]
    public async Task RemoveCandleFile_BTC_USD_LeavesOtherIntervals()
    {
        const string symbol = "BTC/USD";
        var start = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        await _repo.StoreCandlesAsync(symbol, 1, MakeCandles(start, 1, 5));
        await _repo.StoreCandlesAsync(symbol, 5, MakeCandles(start, 5, 5));

        var files1m = await _repo.GetCandleFilesAsync(symbol, 1);
        Assert.Single(files1m);
        await _repo.RemoveCandleFileAsync(files1m[0]);

        var remaining1m = await _repo.GetCandleFilesAsync(symbol, 1);
        var remaining5m = await _repo.GetCandleFilesAsync(symbol, 5);

        Assert.Empty(remaining1m);
        Assert.Single(remaining5m);
    }


    [Fact]
    public async Task RebuildFileIndex_PicksUpExternallyAddedBlobs()
    {
        const string symbol = "AZDOT";
        const int interval  = 60;
        var start   = new DateTime(2024, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeCandles(start, interval, 4);

        // Store via a second repo instance that shares the same container + prefix
        var repo2 = new AzureBlobCandlesRepository(_container, prefix: _prefix);
        await repo2.StoreCandlesAsync(symbol, interval, candles);

        // _repo index has not seen those blobs yet
        var before = await _repo.GetCandleFilesAsync(symbol, interval);
        Assert.Empty(before);

        await _repo.RebuildFileIndexAsync();

        var after = await _repo.GetCandleFilesAsync(symbol, interval);
        Assert.Single(after);
    }
}

/// <summary>Thrown to signal a test should be skipped (xUnit does not have a built-in skip exception).</summary>
public class SkipException(string reason) : Exception(reason) { }

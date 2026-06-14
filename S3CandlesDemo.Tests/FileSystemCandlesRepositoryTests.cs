using S3CandlesDemo.Candles;
using Xunit.Abstractions;

namespace S3CandlesDemo.Tests;

public class FileSystemCandlesRepositoryTests
{
    private readonly ITestOutputHelper _output;

    public FileSystemCandlesRepositoryTests(ITestOutputHelper output)
    {
        _output = output;
    }
    [Fact]
    public async Task GetAndRemoveMethods_WorkCorrectly()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "fsrepo_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var repo = new FileSystemCandlesRepository(baseDir);
            string symbol = "TEST2";
            int interval = 10;
            DateTime start = new DateTime(2024, 1, 2, 9, 30, 0, DateTimeKind.Utc);
            var candles = Enumerable.Range(0, 5).Select(i => new Candle
            {
                Timestamp = start.AddMinutes(i * interval),
                Open = i,
                High = i + 0.5,
                Low = i - 0.5,
                Close = i + 0.1,
                Volume = i * 100,
                TradeCount = i
            }).ToList();

            await repo.StoreCandlesAsync(symbol, interval, candles);
            var files = await repo.GetCandleFilesAsync(symbol, interval);
            Assert.NotEmpty(files);
            // File path is full path, not just file name, so skip symbol assertion

            // Remove a single file
            await repo.RemoveCandleFileAsync(files[0]);
            var filesAfterRemove = await repo.GetCandleFilesAsync(symbol, interval);
            Assert.True(filesAfterRemove.Count < files.Count);

            // Remove all
            await repo.RemoveCandleFilesAsync(symbol, interval);
            var filesAfterAllRemoved = await repo.GetCandleFilesAsync(symbol, interval);
            Assert.Empty(filesAfterAllRemoved);
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch { }
        }
    }
    [Fact]
    public async Task StoreAndFetch_AllCandles_Returned()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "fsrepo_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var repo = new FileSystemCandlesRepository(baseDir);
            string symbol = "TEST";
            int interval = 5;
            DateTime start = new DateTime(2024, 1, 2, 9, 30, 0, DateTimeKind.Utc);
            var candles = Enumerable.Range(0, 20).Select(i => new Candle
            {
                Timestamp = start.AddMinutes(i * interval),
                Open = i,
                High = i + 0.5,
                Low = i - 0.5,
                Close = i + 0.1,
                Volume = i * 100,
                TradeCount = i
            }).ToList();

            await repo.StoreCandlesAsync(symbol, interval, candles);

            var fetched = new List<Candle>();
            await foreach (var c in repo.FetchCandlesAsync(symbol, interval, start, candles.Last().Timestamp))
                fetched.Add(c);

            Assert.Equal(candles.Count, fetched.Count);
            for (int i = 0; i < candles.Count; i++)
            {
                Assert.Equal(candles[i].Timestamp, fetched[i].Timestamp);
                Assert.Equal(candles[i].Open, fetched[i].Open);
            }
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StoreInTwoFiles_AndFetch_FromMiddle_ReturnsSubset()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "fsrepo_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var repo = new FileSystemCandlesRepository(baseDir);
            string symbol = "TWO";
            int interval = 5;
            DateTime start = new DateTime(2024, 1, 3, 9, 30, 0, DateTimeKind.Utc);

            var firstBatch = Enumerable.Range(0, 10).Select(i => new Candle
            {
                Timestamp = start.AddMinutes(i * interval),
                Open = i,
                High = i + 0.1,
                Low = i - 0.1,
                Close = i + 0.01,
                Volume = i * 10,
                TradeCount = i
            }).ToList();

            var secondBatch = Enumerable.Range(10, 10).Select(i => new Candle
            {
                Timestamp = start.AddMinutes(i * interval),
                Open = i,
                High = i + 0.1,
                Low = i - 0.1,
                Close = i + 0.01,
                Volume = i * 10,
                TradeCount = i
            }).ToList();

            await repo.StoreCandlesAsync(symbol, interval, firstBatch);
            await repo.StoreCandlesAsync(symbol, interval, secondBatch);

            DateTime from = start.AddMinutes(7 * interval);
            DateTime to = start.AddMinutes(15 * interval);

            var fetched = new List<Candle>();
            await foreach (var c in repo.FetchCandlesAsync(symbol, interval, from, to))
                fetched.Add(c);

            // expected candles: indexes 7..15 inclusive -> 9 items
            Assert.Equal(9, fetched.Count);
            Assert.Equal(from, fetched.First().Timestamp);
            Assert.Equal(start.AddMinutes(15 * interval), fetched.Last().Timestamp);

            var allCandles = firstBatch.Concat(secondBatch).ToList();
            for (int i = 0; i < fetched.Count; i++)
            {
                var expectedCandle = allCandles[7 + i];
                Assert.Equal(expectedCandle.Timestamp, fetched[i].Timestamp);
                Assert.Equal(expectedCandle.Open, fetched[i].Open);
                Assert.Equal(expectedCandle.High, fetched[i].High);
                Assert.Equal(expectedCandle.Low, fetched[i].Low);
                Assert.Equal(expectedCandle.Close, fetched[i].Close);
                Assert.Equal(expectedCandle.Volume, fetched[i].Volume);
                Assert.Equal(expectedCandle.TradeCount, fetched[i].TradeCount);
            }
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SymbolWithSlash_StoreAndFetch_WorksCorrectly()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "fsrepo_slash_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var repo = new FileSystemCandlesRepository(baseDir);
            const string symbol = "BTC/USD";
            const int interval = 60;
            var start = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var candles = Enumerable.Range(0, 5).Select(i => new Candle
            {
                Timestamp = start.AddMinutes(i * interval),
                Open = 60000 + i,
                High = 60100 + i,
                Low = 59900 + i,
                Close = 60050 + i,
                Volume = 1000 + i,
                TradeCount = 10 + i
            }).ToList();

            await repo.StoreCandlesAsync(symbol, interval, candles);

            // No physical file on disk should contain a raw '/'
            var diskFiles = Directory.GetFiles(baseDir);
            Assert.All(diskFiles, f => Assert.DoesNotContain("/", Path.GetFileName(f)));
            // The encoded form must appear
            Assert.Contains(diskFiles, f => Path.GetFileName(f).Contains("BTC%2FUSD"));

            // Fetch returns correct candles with original symbol
            var fetched = new List<Candle>();
            await foreach (var c in repo.FetchCandlesAsync(symbol, interval, start, candles.Last().Timestamp))
                fetched.Add(c);
            Assert.Equal(candles.Count, fetched.Count);
            for (int i = 0; i < candles.Count; i++)
                Assert.Equal(candles[i].Timestamp, fetched[i].Timestamp);

            // GetCandleFilesAsync returns decoded path (no %2F in path is not required, but symbol in index is decoded)
            var files = await repo.GetCandleFilesAsync(symbol, interval);
            Assert.NotEmpty(files);
            // The path exposed to callers should NOT contain the raw slash from encoded form in a way
            // that would confuse the OS; verify we can remove via the returned file info
            await repo.RemoveCandleFileAsync(files[0]);
            var filesAfter = await repo.GetCandleFilesAsync(symbol, interval);
            Assert.Empty(filesAfter);
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StoreCandlesAsync_SingleGap_InsertsOneFlatCandle()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "fsrepo_flat_single_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var repo = new FileSystemCandlesRepository(baseDir);
            const string symbol = "FLAT";
            const int interval = 5;
            var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            // Two candles with one missing slot between them (t0+5min is skipped)
            var candles = new List<Candle>
            {
                new Candle { Timestamp = t0,                          Open = 100, High = 110, Low = 90,  Close = 105, Volume = 1000, TradeCount = 10 },
                new Candle { Timestamp = t0.AddMinutes(2 * interval), Open = 200, High = 210, Low = 190, Close = 205, Volume = 2000, TradeCount = 20 },
            };

            await repo.StoreCandlesAsync(symbol, interval, candles);

            var fetched = new List<Candle>();
            await foreach (var c in repo.FetchCandlesAsync(symbol, interval, t0, t0.AddMinutes(2 * interval)))
                fetched.Add(c);

            // original + 1 flat fill + original = 3
            Assert.Equal(3, fetched.Count);

            var flat = fetched[1];
            Assert.Equal(t0.AddMinutes(interval), flat.Timestamp);
            Assert.Equal(105, flat.Open);    // previous candle's Close
            Assert.Equal(105, flat.High);
            Assert.Equal(105, flat.Low);
            Assert.Equal(105, flat.Close);
            Assert.Equal(0, flat.Volume);
            Assert.Equal(0, flat.TradeCount);
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StoreCandlesAsync_MultipleGap_InsertsMultipleFlatCandles()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "fsrepo_flat_multi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var repo = new FileSystemCandlesRepository(baseDir);
            const string symbol = "FLATMULTI";
            const int interval = 1;
            var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            // Two candles 4 intervals apart → 3 missing slots
            var candles = new List<Candle>
            {
                new Candle { Timestamp = t0,                          Open = 100, High = 110, Low = 90,  Close = 108, Volume = 500, TradeCount = 5 },
                new Candle { Timestamp = t0.AddMinutes(4 * interval), Open = 200, High = 220, Low = 180, Close = 210, Volume = 800, TradeCount = 8 },
            };

            await repo.StoreCandlesAsync(symbol, interval, candles);

            var fetched = new List<Candle>();
            await foreach (var c in repo.FetchCandlesAsync(symbol, interval, t0, t0.AddMinutes(4 * interval)))
                fetched.Add(c);

            // original + 3 flat fills + original = 5
            Assert.Equal(5, fetched.Count);

            for (int i = 1; i <= 3; i++)
            {
                var flat = fetched[i];
                Assert.Equal(t0.AddMinutes(i * interval), flat.Timestamp);
                Assert.Equal(108, flat.Open);    // previous candle's Close propagated through chain
                Assert.Equal(108, flat.High);
                Assert.Equal(108, flat.Low);
                Assert.Equal(108, flat.Close);
                Assert.Equal(0, flat.Volume);
                Assert.Equal(0, flat.TradeCount);
            }
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StoreCandlesAsync_NoGap_NoFlatCandlesInserted()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "fsrepo_flat_nogap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var repo = new FileSystemCandlesRepository(baseDir);
            const string symbol = "NOGAP";
            const int interval = 5;
            var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var candles = Enumerable.Range(0, 5).Select(i => new Candle
            {
                Timestamp = t0.AddMinutes(i * interval),
                Open = 100 + i, High = 110 + i, Low = 90 + i, Close = 105 + i,
                Volume = 1000, TradeCount = 10
            }).ToList();

            await repo.StoreCandlesAsync(symbol, interval, candles);

            var fetched = new List<Candle>();
            await foreach (var c in repo.FetchCandlesAsync(symbol, interval, t0, candles.Last().Timestamp))
                fetched.Add(c);

            Assert.Equal(candles.Count, fetched.Count);
            for (int i = 0; i < candles.Count; i++)
                Assert.Equal(candles[i].Timestamp, fetched[i].Timestamp);
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StoreCandlesAsync_GapInMiddleOfSequence_InsertsFlatsAtCorrectPosition()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "fsrepo_flat_mid_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var repo = new FileSystemCandlesRepository(baseDir);
            const string symbol = "MIDGAP";
            const int interval = 10;
            var t0 = new DateTime(2024, 3, 15, 8, 0, 0, DateTimeKind.Utc);

            // Candles at t0, t0+10, then skip t0+20, resume at t0+30 and t0+40
            var candles = new List<Candle>
            {
                new Candle { Timestamp = t0,                          Open = 1, High = 2, Low = 0.5, Close = 1.5, Volume = 100, TradeCount = 1 },
                new Candle { Timestamp = t0.AddMinutes(interval),     Open = 2, High = 3, Low = 1.5, Close = 2.5, Volume = 200, TradeCount = 2 },
                // t0+2*interval is missing
                new Candle { Timestamp = t0.AddMinutes(3 * interval), Open = 3, High = 4, Low = 2.5, Close = 3.5, Volume = 300, TradeCount = 3 },
                new Candle { Timestamp = t0.AddMinutes(4 * interval), Open = 4, High = 5, Low = 3.5, Close = 4.5, Volume = 400, TradeCount = 4 },
            };

            await repo.StoreCandlesAsync(symbol, interval, candles);

            var fetched = new List<Candle>();
            await foreach (var c in repo.FetchCandlesAsync(symbol, interval, t0, t0.AddMinutes(4 * interval)))
                fetched.Add(c);

            // 4 original + 1 flat fill = 5
            Assert.Equal(5, fetched.Count);

            var flat = fetched[2];
            Assert.Equal(t0.AddMinutes(2 * interval), flat.Timestamp);
            Assert.Equal(2.5, flat.Open);    // Close of candle at t0+interval
            Assert.Equal(2.5, flat.High);
            Assert.Equal(2.5, flat.Low);
            Assert.Equal(2.5, flat.Close);
            Assert.Equal(0, flat.Volume);
            Assert.Equal(0, flat.TradeCount);
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch { }
        }
    }

    //[Fact] this test is one time utility to verify we can read candles from a specific file on disk, not meant for regular test runs
    public async Task TestSpecificFile()
    {
        var candlesPath = "/home/denivlah/Desktop/Projects/s3CandlesDemo/s3CandlesDemo/candles";
        var repo = new FileSystemCandlesRepository(candlesPath);
        await repo.RebuildFileIndexAsync();
        var from = new DateTime(2024, 06, 5, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2024, 06, 30, 0, 0, 0, DateTimeKind.Utc);

        var fetched = new List<Candle>();

        await foreach (var c in repo.FetchCandlesAsync("BTC/USDT", 1, from, to))
            fetched.Add(c);


        Assert.NotEmpty(fetched);
        foreach (var c in fetched)
        {
            _output.WriteLine($"{c.Timestamp:yyyy-MM-dd HH:mm} O:{c.Open} H:{c.High} L:{c.Low} C:{c.Close} V:{c.Volume} T:{c.TradeCount}");
        }
    }
}

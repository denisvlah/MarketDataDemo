using S3CandlesDemo.Candles;

namespace S3CandlesDemo.Tests;

public class FileSystemCandlesRepositoryTests
{
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
}

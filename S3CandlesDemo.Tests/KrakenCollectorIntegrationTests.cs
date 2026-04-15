using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using S3CandlesDemo.Candles;
using S3CandlesDemo.KrakenCollector;

namespace S3CandlesDemo.Tests;

/// <summary>
/// A fake IKrakenOhlcService that returns deterministic candle data for testing.
/// Simulates Kraken's behavior: max 720 entries per call, last entry discarded by the service.
/// </summary>
public class FakeKrakenOhlcService : IKrakenOhlcService
{
    private readonly int _maxCandlesPerBatch;

    public FakeKrakenOhlcService(int maxCandlesPerBatch = 719)
    {
        _maxCandlesPerBatch = maxCandlesPerBatch;
    }

    /// <summary>Number of API calls made (for verification).</summary>
    public int CallCount { get; private set; }

    /// <summary>The cutoff date — fake service won't return candles at or after this time.</summary>
    public DateTime DataEndUtc { get; set; } = DateTime.UtcNow.Date;

    public Task<KrakenOhlcBatch> GetOhlcAsync(string krakenPair, int intervalMinutes, DateTime since, CancellationToken ct = default)
    {
        CallCount++;

        var candles = new List<Candle>();
        var current = since;

        for (int i = 0; i < _maxCandlesPerBatch; i++)
        {
            if (current >= DataEndUtc)
                break;

            candles.Add(new Candle
            {
                Timestamp = current,
                Open = 100.0 + i * 0.1,
                High = 101.0 + i * 0.1,
                Low = 99.0 + i * 0.1,
                Close = 100.5 + i * 0.1,
                Volume = 1000.0 + i,
                TradeCount = 50 + i
            });
            current = current.AddMinutes(intervalMinutes);
        }

        DateTime? lastTimestamp = candles.Count > 0 ? candles.Last().Timestamp : null;
        return Task.FromResult(new KrakenOhlcBatch(candles, lastTimestamp));
    }
}

[Collection("Minio collection")]
public class KrakenCollectorIntegrationTests
{
    private readonly MinioFixture _fixture;

    public KrakenCollectorIntegrationTests(MinioFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task EnsureBucketAsync(string bucket)
    {
        if (_fixture.Client == null) throw new InvalidOperationException("S3 client is not initialized.");
        var buckets = await _fixture.Client.ListBucketsAsync();
        if (buckets.Buckets.All(b => b.BucketName != bucket))
            await _fixture.Client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
    }

    private async Task CleanupBucketAsync(string bucket)
    {
        if (_fixture.Client == null) return;
        try
        {
            var objects = await _fixture.Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
            foreach (var obj in objects.S3Objects)
                await _fixture.Client.DeleteObjectAsync(bucket, obj.Key);
        }
        catch { }
    }

    [Fact]
    public async Task Collector_FetchesAndStoresCandles_ToS3()
    {
        var bucket = "test-collector-basic";
        await EnsureBucketAsync(bucket);

        try
        {
            var repo = new S3CandlesRepository(bucket, "collector-test", _fixture.Client);
            var fakeKraken = new FakeKrakenOhlcService(maxCandlesPerBatch: 10);

            var start = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var cutoff = new DateTime(2024, 6, 2, 0, 0, 0, DateTimeKind.Utc); // 24 hours of 60-min candles = 24 candles
            fakeKraken.DataEndUtc = cutoff;

            var job = new CollectorJobConfig("TESTBTC", "XBTUSD", 60, start);

            using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
            var collector = new CandleCollector(repo, fakeKraken, loggerFactory.CreateLogger<CandleCollector>(), TimeSpan.Zero);

            await collector.RunJobAsync(job, cutoff);

            // Verify candles stored in S3
            var files = await repo.GetCandleFilesAsync("TESTBTC", 60);
            Assert.NotEmpty(files);

            var fetched = new List<Candle>();
            await foreach (var c in repo.FetchCandlesAsync("TESTBTC", 60, start, cutoff.AddDays(-1)))
                fetched.Add(c);

            Assert.True(fetched.Count > 0, "Expected candles stored in S3");
            Assert.Equal(start, fetched.First().Timestamp);

            // Verify the fake Kraken service was called
            Assert.True(fakeKraken.CallCount >= 1, "Kraken API should have been called at least once");
        }
        finally
        {
            await CleanupBucketAsync(bucket);
        }
    }

    [Fact]
    public async Task Collector_ResumesFromLastStoredCandle()
    {
        var bucket = "test-collector-resume";
        await EnsureBucketAsync(bucket);

        try
        {
            var repo = new S3CandlesRepository(bucket, "resume-test", _fixture.Client);
            var start = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            var cutoff = new DateTime(2024, 7, 2, 0, 0, 0, DateTimeKind.Utc);

            // Pre-store some candles (first 6 hours)
            var existingCandles = Enumerable.Range(0, 6).Select(i => new Candle
            {
                Timestamp = start.AddHours(i),
                Open = 100 + i,
                High = 101 + i,
                Low = 99 + i,
                Close = 100.5 + i,
                Volume = 1000 + i,
                TradeCount = 10 + i
            }).ToList();
            await repo.StoreCandlesAsync("RESUMETEST", 60, existingCandles);

            // Now run collector — it should resume from hour 6, not hour 0
            var fakeKraken = new FakeKrakenOhlcService(maxCandlesPerBatch: 50);
            fakeKraken.DataEndUtc = cutoff;
            var job = new CollectorJobConfig("RESUMETEST", "XBTUSD", 60, start);

            using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
            var collector = new CandleCollector(repo, fakeKraken, loggerFactory.CreateLogger<CandleCollector>(), TimeSpan.Zero);

            await collector.RunJobAsync(job, cutoff);

            // Fetch all candles — should cover the full 24 hours
            var allCandles = new List<Candle>();
            await foreach (var c in repo.FetchCandlesAsync("RESUMETEST", 60, start, cutoff.AddMinutes(-1)))
                allCandles.Add(c);

            // Should have the pre-existing 6 + newly fetched candles covering remaining hours
            Assert.True(allCandles.Count >= 6, $"Expected at least 6 candles, got {allCandles.Count}");
            Assert.Equal(start, allCandles.First().Timestamp);
        }
        finally
        {
            await CleanupBucketAsync(bucket);
        }
    }

    [Fact]
    public async Task Collector_RunAllAsync_MultipleJobs_AllSucceed()
    {
        var bucket = "test-collector-multi";
        await EnsureBucketAsync(bucket);

        try
        {
            var repo = new S3CandlesRepository(bucket, "multi-test", _fixture.Client);
            var fakeKraken = new FakeKrakenOhlcService(maxCandlesPerBatch: 20);

            var start = new DateTime(2024, 8, 1, 0, 0, 0, DateTimeKind.Utc);
            var cutoff = new DateTime(2024, 8, 1, 12, 0, 0, DateTimeKind.Utc); // 12 hours
            fakeKraken.DataEndUtc = cutoff;

            var jobs = new List<CollectorJobConfig>
            {
                new("PAIR1", "XBTUSD", 60, start),
                new("PAIR2", "ETHUSD", 60, start),
            };

            using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
            var collector = new CandleCollector(repo, fakeKraken, loggerFactory.CreateLogger<CandleCollector>(), TimeSpan.Zero);

            var success = await collector.RunAllAsync(jobs, cutoff);
            Assert.True(success, "All jobs should succeed");

            // Both pairs should have candles
            var pair1Files = await repo.GetCandleFilesAsync("PAIR1", 60);
            var pair2Files = await repo.GetCandleFilesAsync("PAIR2", 60);
            Assert.NotEmpty(pair1Files);
            Assert.NotEmpty(pair2Files);
        }
        finally
        {
            await CleanupBucketAsync(bucket);
        }
    }

    [Fact]
    public async Task Collector_NoCandlesAvailable_ExitsGracefully()
    {
        var bucket = "test-collector-nodata";
        await EnsureBucketAsync(bucket);

        try
        {
            var repo = new S3CandlesRepository(bucket, "nodata-test", _fixture.Client);

            // Set data end before start — no candles will be returned
            var fakeKraken = new FakeKrakenOhlcService(maxCandlesPerBatch: 10);
            var start = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            fakeKraken.DataEndUtc = start; // no data available

            var job = new CollectorJobConfig("NODATA", "XBTUSD", 60, start);

            using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
            var collector = new CandleCollector(repo, fakeKraken, loggerFactory.CreateLogger<CandleCollector>(), TimeSpan.Zero);

            await collector.RunJobAsync(job, start.AddDays(1));

            // Should have no files stored
            var files = await repo.GetCandleFilesAsync("NODATA", 60);
            Assert.Empty(files);
        }
        finally
        {
            await CleanupBucketAsync(bucket);
        }
    }

    [Fact]
    public async Task Collector_PaginatesThroughLargeDataset()
    {
        var bucket = "test-collector-paginate";
        await EnsureBucketAsync(bucket);

        try
        {
            var repo = new S3CandlesRepository(bucket, "paginate-test", _fixture.Client);

            // Small batch size forces multiple API calls (pagination)
            var fakeKraken = new FakeKrakenOhlcService(maxCandlesPerBatch: 5);

            var start = new DateTime(2024, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            var cutoff = new DateTime(2024, 10, 2, 0, 0, 0, DateTimeKind.Utc); // 24 hours / 60-min = 24 candles
            fakeKraken.DataEndUtc = cutoff;

            var job = new CollectorJobConfig("PAGTEST", "XBTUSD", 60, start);

            using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
            var collector = new CandleCollector(repo, fakeKraken, loggerFactory.CreateLogger<CandleCollector>(), TimeSpan.Zero);

            await collector.RunJobAsync(job, cutoff);

            // Should have made multiple API calls
            Assert.True(fakeKraken.CallCount > 1, $"Expected multiple API calls due to pagination, got {fakeKraken.CallCount}");

            // Fetch all candles and verify count
            var allCandles = new List<Candle>();
            await foreach (var c in repo.FetchCandlesAsync("PAGTEST", 60, start, cutoff.AddMinutes(-1)))
                allCandles.Add(c);

            Assert.Equal(24, allCandles.Count);
            Assert.Equal(start, allCandles.First().Timestamp);
            Assert.Equal(cutoff.AddHours(-1), allCandles.Last().Timestamp);
        }
        finally
        {
            await CleanupBucketAsync(bucket);
        }
    }

    [Fact]
    public async Task Collector_StoresWithCorrectAssetPairName_NotKrakenName()
    {
        var bucket = "test-collector-naming";
        await EnsureBucketAsync(bucket);

        try
        {
            var repo = new S3CandlesRepository(bucket, "naming-test", _fixture.Client);
            var fakeKraken = new FakeKrakenOhlcService(maxCandlesPerBatch: 10);

            var start = new DateTime(2024, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            var cutoff = new DateTime(2024, 11, 1, 5, 0, 0, DateTimeKind.Utc);
            fakeKraken.DataEndUtc = cutoff;

            // Asset pair "BTCUSD" but Kraken uses "XBTUSD"
            var job = new CollectorJobConfig("BTCUSD", "XBTUSD", 60, start);

            using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
            var collector = new CandleCollector(repo, fakeKraken, loggerFactory.CreateLogger<CandleCollector>(), TimeSpan.Zero);

            await collector.RunJobAsync(job, cutoff);

            // Should be stored under "BTCUSD", not "XBTUSD"
            var btcFiles = await repo.GetCandleFilesAsync("BTCUSD", 60);
            Assert.NotEmpty(btcFiles);

            var xbtFiles = await repo.GetCandleFilesAsync("XBTUSD", 60);
            Assert.Empty(xbtFiles);
        }
        finally
        {
            await CleanupBucketAsync(bucket);
        }
    }

    [Fact]
    public async Task Collector_BackfillsWhenStartDateMovedEarlier()
    {
        var bucket = "test-collector-backfill";
        await EnsureBucketAsync(bucket);

        try
        {
            var repo = new S3CandlesRepository(bucket, "backfill-test", _fixture.Client);

            // Pre-store candles for hours 6-11 (as if original start was hour 6)
            var existingStart = new DateTime(2024, 12, 1, 6, 0, 0, DateTimeKind.Utc);
            var existingCandles = Enumerable.Range(0, 6).Select(i => new Candle
            {
                Timestamp = existingStart.AddHours(i),
                Open = 200 + i,
                High = 201 + i,
                Low = 199 + i,
                Close = 200.5 + i,
                Volume = 2000 + i,
                TradeCount = 20 + i
            }).ToList();
            await repo.StoreCandlesAsync("BACKFILL", 60, existingCandles);

            // Now run collector with an earlier start date (hour 0) — should backfill hours 0-5
            var earlierStart = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc);
            var cutoff = new DateTime(2024, 12, 1, 12, 0, 0, DateTimeKind.Utc);

            var fakeKraken = new FakeKrakenOhlcService(maxCandlesPerBatch: 50);
            fakeKraken.DataEndUtc = cutoff;

            var job = new CollectorJobConfig("BACKFILL", "XBTUSD", 60, earlierStart);

            using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
            var collector = new CandleCollector(repo, fakeKraken, loggerFactory.CreateLogger<CandleCollector>(), TimeSpan.Zero);

            await collector.RunJobAsync(job, cutoff);

            // Fetch all candles — should now cover 0:00 through 11:00 (12 hours)
            var allCandles = new List<Candle>();
            await foreach (var c in repo.FetchCandlesAsync("BACKFILL", 60, earlierStart, cutoff.AddMinutes(-1)))
                allCandles.Add(c);

            // Verify backfill: first candle should be at the earlier start time
            Assert.True(allCandles.Count >= 12, $"Expected at least 12 candles (backfill + existing), got {allCandles.Count}");
            Assert.Equal(earlierStart, allCandles.First().Timestamp);

            // Verify the Kraken API was called (for the backfill range)
            Assert.True(fakeKraken.CallCount >= 1, "Kraken API should have been called for backfill");
        }
        finally
        {
            await CleanupBucketAsync(bucket);
        }
    }

    [Fact]
    public async Task ReadFromS3_ParsesUploadedCsvCorrectly()
    {
        var bucket = "test-csv-config";
        await EnsureBucketAsync(bucket);

        try
        {
            // Upload a config CSV to MinIO
            var csvContent = "BTCUSD,XBTUSD,60,2024-01-01\nETHUSD,ETHUSD,1440,2024-06-01\nSOLUSD,SOLUSD,15,2025-01-01\n";
            var key = "config/kraken-collector-config.csv";

            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csvContent));
            await _fixture.Client!.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = stream
            });

            // Read it back via CsvConfigReader.ReadFromS3Async
            var jobs = await CsvConfigReader.ReadFromS3Async(_fixture.Client, bucket, key);

            Assert.Equal(3, jobs.Count);
            Assert.Equal("BTCUSD", jobs[0].AssetPair);
            Assert.Equal("XBTUSD", jobs[0].KrakenPair);
            Assert.Equal(60, jobs[0].IntervalMinutes);
            Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), jobs[0].StartDate);

            Assert.Equal("ETHUSD", jobs[1].AssetPair);
            Assert.Equal(1440, jobs[1].IntervalMinutes);

            Assert.Equal("SOLUSD", jobs[2].AssetPair);
            Assert.Equal(15, jobs[2].IntervalMinutes);
            Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), jobs[2].StartDate);
        }
        finally
        {
            await CleanupBucketAsync(bucket);
        }
    }
}

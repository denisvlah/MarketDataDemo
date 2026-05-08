using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;


namespace S3CandlesDemo.Tests;

// Fixture for MinIO container
public class MinioFixture : IAsyncLifetime
{
    public const string AccessKey = "minioadmin";
    public const string SecretKey = "minioadmin";
    public const int MinioPort = 9000;
    public TestcontainersContainer? Container { get; private set; }
    public string? ServiceUrl { get; private set; }
    public AmazonS3Client? Client { get; private set; }

    public async Task InitializeAsync()
    {
        // Try to connect to already running MinIO
        try
        {
            var testClient = new AmazonS3Client(AccessKey, SecretKey, new AmazonS3Config { ServiceURL = $"http://localhost:{MinioPort}", ForcePathStyle = true, UseHttp = true });
            var _ = await testClient.ListBucketsAsync();
            ServiceUrl = $"http://localhost:{MinioPort}";
            Client = testClient;
            return;
        }
        catch
        {
            // ignored
        }

        Container = new TestcontainersBuilder<TestcontainersContainer>()
            .WithImage("minio/minio:latest")
            .WithName($"minio-test-{Guid.NewGuid()}")
            .WithEnvironment("MINIO_ROOT_USER", AccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
            .WithCommand("server", "/data")
            .WithPortBinding(MinioPort, true)
            .Build();
        await Container.StartAsync();
        await Task.Delay(2000); // Wait for MinIO to start
        var port = Container.GetMappedPublicPort(MinioPort);
        ServiceUrl = $"http://{Container.Hostname}:{port}";
        Client = new AmazonS3Client(AccessKey, SecretKey, new AmazonS3Config { ServiceURL = ServiceUrl, ForcePathStyle = true, UseHttp = true });
        // Ensure bucket exists for integration tests
        var bucketName = "minio-test-bucket";
        var minioBuckets = await Client.ListBucketsAsync();
        if (minioBuckets.Buckets == null || !minioBuckets.Buckets.Any(b => b.BucketName == bucketName)) 
        {
            await Client.PutBucketAsync(new PutBucketRequest { BucketName = bucketName });
        }
    }

    public async Task DisposeAsync()
    {
        if (Container != null)
        {
            try { await Container.CleanUpAsync(); }
            catch { /* Container may already be removed or zombie; ignore cleanup errors */ }
        }
        Client?.Dispose();
    }
}

[CollectionDefinition("Minio collection")]
public class MinioCollection : ICollectionFixture<MinioFixture> { }

[Collection("Minio collection")]
public class S3CandlesRepositoryIntegrationTests
{
    [Fact]
    public async Task GetAndRemoveMethods_WorkCorrectly()
    {
        var bucket = "test-bucket-getremove";
        await EnsureBucketAsync(bucket);
        var repo = new Candles.S3CandlesRepository(bucket, prefix: null, client: _fixture.Client);
        string symbol = "GETREMOVE";
        int interval = 5;
        DateTime start = DateTime.UtcNow.Date.AddHours(8);
        var candles = Enumerable.Range(0, 5).Select(i => new Candles.Candle
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

        // Remove a single file
        await repo.RemoveCandleFileAsync(files[0]);
        var filesAfterRemove = await repo.GetCandleFilesAsync(symbol, interval);
        Assert.True(filesAfterRemove.Count < files.Count);

        // Remove all
        await repo.RemoveCandleFilesAsync(symbol, interval);
        var filesAfterAllRemoved = await repo.GetCandleFilesAsync(symbol, interval);
        Assert.Empty(filesAfterAllRemoved);
        await CleanUpAfterTest(bucket);
    }
    private readonly MinioFixture _fixture;
    private List<string> _buckets = new();

    public S3CandlesRepositoryIntegrationTests(MinioFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task CleanupBucketAsync(string bucket)
    {
        if (_fixture.Client == null) throw new InvalidOperationException("S3 client is not initialized.");
        var objects = await _fixture.Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
        foreach (var obj in objects.S3Objects ?? []) await _fixture.Client.DeleteObjectAsync(bucket, obj.Key);
    }

    private async Task EnsureBucketAsync(string bucket)
    {
        if (_fixture.Client == null) throw new InvalidOperationException("S3 client is not initialized.");
        var buckets = await _fixture.Client.ListBucketsAsync();
        if (buckets.Buckets.All(b => b.BucketName != bucket)) await _fixture.Client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        if (!_buckets.Contains(bucket))
            _buckets.Add(bucket);
    }

    private async Task CleanUpAfterTest(string bucket)
    {
        await CleanupBucketAsync(bucket);
    }

    [Fact]
    public async Task FetchCandles_FromMiddleOfFile_Works()
    {
        var bucket = "test-bucket-midfile";
        await EnsureBucketAsync(bucket);
        var repo = new Candles.S3CandlesRepository(bucket, prefix: null, client: _fixture.Client);
        string symbol = "MIDFILE";
        int interval = 10;
        DateTime start = DateTime.UtcNow.Date.AddHours(8);
        var candles = Enumerable.Range(0, 20).Select(i => new Candles.Candle
        {
            Timestamp = start.AddMinutes(i * interval),
            Open = i,
            High = i + 0.3,
            Low = i - 0.3,
            Close = i + 0.07,
            Volume = i * 20,
            TradeCount = i
        }).ToList();
        await repo.StoreCandlesAsync(symbol, interval, candles);
        DateTime from = candles[8].Timestamp;
        DateTime to = candles[15].Timestamp;
        var fetched = new List<Candles.Candle>();
        await foreach (var c in repo.FetchCandlesAsync(symbol, interval, from, to))
            fetched.Add(c);
        Assert.Equal(8, fetched.Count);
        for (int i = 0; i < fetched.Count; i++)
        {
            var expected = candles[8 + i];
            Assert.Equal(expected.Timestamp, fetched[i].Timestamp);
            Assert.Equal(expected.Open, fetched[i].Open);
            Assert.Equal(expected.High, fetched[i].High);
            Assert.Equal(expected.Low, fetched[i].Low);
            Assert.Equal(expected.Close, fetched[i].Close);
            Assert.Equal(expected.Volume, fetched[i].Volume);
            Assert.Equal(expected.TradeCount, fetched[i].TradeCount);
        }
        await CleanUpAfterTest(bucket);
    }

    [Fact]
    public async Task StoreInTwoBatches_AndFetchSubset_Works()
    {
        var bucket = "test-bucket-twobatch";
        await EnsureBucketAsync(bucket);
        var repo = new Candles.S3CandlesRepository(bucket, prefix: null, client: _fixture.Client);
        string symbol = "MINIO2";
        int interval = 5;
        DateTime start = DateTime.UtcNow.Date.AddHours(9);
        var firstBatch = Enumerable.Range(0, 10).Select(i => new Candles.Candle
        {
            Timestamp = start.AddMinutes(i * interval),
            Open = i,
            High = i + 0.1,
            Low = i - 0.1,
            Close = i + 0.01,
            Volume = i * 10,
            TradeCount = i
        }).ToList();
        var secondBatch = Enumerable.Range(10, 10).Select(i => new Candles.Candle
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
        var fetched = new List<Candles.Candle>();
        await foreach (var c in repo.FetchCandlesAsync(symbol, interval, from, to))
            fetched.Add(c);
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
        await CleanUpAfterTest(bucket);
    }

    [Fact]
    public async Task StoreAndFetch_WithPrefix_Works()
    {
        var bucket = "test-bucket-prefix";
        var prefix = "candlesdata";
        await EnsureBucketAsync(bucket);
        var repo = new Candles.S3CandlesRepository(bucket, prefix: prefix, client: _fixture.Client);
        string symbol = "MINIOPFX";
        int interval = 15;
        DateTime start = DateTime.UtcNow.Date.AddHours(10);
        var candles = Enumerable.Range(0, 8).Select(i => new Candles.Candle
        {
            Timestamp = start.AddMinutes(i * interval),
            Open = i,
            High = i + 0.2,
            Low = i - 0.2,
            Close = i + 0.05,
            Volume = i * 50,
            TradeCount = i
        }).ToList();
        await repo.StoreCandlesAsync(symbol, interval, candles);
        var fetched = new List<Candles.Candle>();
        await foreach (var c in repo.FetchCandlesAsync(symbol, interval, candles.First().Timestamp, candles.Last().Timestamp))
            fetched.Add(c);
        Assert.Equal(candles.Count, fetched.Count);
        Assert.Equal(candles.First().Timestamp, fetched.First().Timestamp);
        Assert.Equal(candles.Last().Timestamp, fetched.Last().Timestamp);
        await CleanUpAfterTest(bucket);
    }

    [Fact]
    public async Task StoreAndFetch_With_Minio_Works()
    {
        var bucket = "test-bucket-basic";
        await EnsureBucketAsync(bucket);
        var repo = new Candles.S3CandlesRepository(bucket, prefix: null, client: _fixture.Client);
        string symbol = "MINIO";
        int interval = 5;
        DateTime start = DateTime.UtcNow.Date.AddHours(9);
        var candles = Enumerable.Range(0, 12).Select(i => new Candles.Candle
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
        var fetched = new List<Candles.Candle>();
        await foreach (var c in repo.FetchCandlesAsync(symbol, interval, candles.First().Timestamp, candles.Last().Timestamp))
            fetched.Add(c);
        Assert.Equal(candles.Count, fetched.Count);
        Assert.Equal(candles.First().Timestamp, fetched.First().Timestamp);
        Assert.Equal(candles.Last().Timestamp, fetched.Last().Timestamp);
        await CleanUpAfterTest(bucket);
    }

    [Fact]
    public async Task StoreLargeStream_200MB_DirectToS3_Works()
    {
        var bucket = "test-bucket-large-stream";
        await EnsureBucketAsync(bucket);
        var repo = new Candles.S3CandlesRepository(bucket, prefix: "large", client: _fixture.Client);

        string symbol = "BIGSTREAM";
        int interval = 1;
        // ~200MB: 200 * 1024 * 1024 / 52 ≈ 4,033,507 candles
        int candleCount = (200 * 1024 * 1024) / Candles.Candle.CandleByteSize;
        DateTime start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Stream candles without materializing them all in memory
        async IAsyncEnumerable<Candles.Candle> GenerateCandles()
        {
            for (int i = 0; i < candleCount; i++)
            {
                yield return new Candles.Candle
                {
                    Timestamp = start.AddMinutes(i * interval),
                    Open = 100.0 + (i % 1000) * 0.01,
                    High = 100.5 + (i % 1000) * 0.01,
                    Low = 99.5 + (i % 1000) * 0.01,
                    Close = 100.1 + (i % 1000) * 0.01,
                    Volume = i * 10.0,
                    TradeCount = i % 500
                };
            }
            await Task.CompletedTask;
        }

        // Store — this exercises multipart upload with ~5MB parts
        await repo.StoreCandlesAsync(symbol, interval, GenerateCandles());

        // Verify the file exists in the index
        var files = await repo.GetCandleFilesAsync(symbol, interval);
        Assert.Single(files);

        // Spot-check: fetch a small window from the middle and verify values
        int midIndex = candleCount / 2;
        DateTime fetchFrom = start.AddMinutes(midIndex * interval);
        DateTime fetchTo = start.AddMinutes((midIndex + 99) * interval);
        var fetched = new List<Candles.Candle>();
        await foreach (var c in repo.FetchCandlesAsync(symbol, interval, fetchFrom, fetchTo))
            fetched.Add(c);

        Assert.Equal(100, fetched.Count);
        Assert.Equal(fetchFrom, fetched.First().Timestamp);
        Assert.Equal(fetchTo, fetched.Last().Timestamp);

        // Verify a few field values from the middle candle
        var sample = fetched[50];
        int expectedI = midIndex + 50;
        Assert.Equal(start.AddMinutes(expectedI * interval), sample.Timestamp);
        Assert.Equal(100.0 + (expectedI % 1000) * 0.01, sample.Open, precision: 10);
        Assert.Equal(100.5 + (expectedI % 1000) * 0.01, sample.High, precision: 10);
        Assert.Equal(99.5 + (expectedI % 1000) * 0.01, sample.Low, precision: 10);
        Assert.Equal(100.1 + (expectedI % 1000) * 0.01, sample.Close, precision: 10);
        Assert.Equal(expectedI * 10.0, sample.Volume, precision: 5);
        Assert.Equal(expectedI % 500, sample.TradeCount);

        await CleanUpAfterTest(bucket);
    }

    [Fact]
    public async Task SymbolWithSlash_StoreAndFetch_WorksCorrectly()
    {
        var bucket = "test-bucket-slash-symbol";
        await EnsureBucketAsync(bucket);
        var repo = new Candles.S3CandlesRepository(bucket, prefix: null, client: _fixture.Client);
        const string symbol = "BTC/USD";
        const int interval = 60;
        var start = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = Enumerable.Range(0, 5).Select(i => new Candles.Candle
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

        // Verify the S3 key is percent-encoded (no raw slash from symbol in the key)
        if (_fixture.Client != null)
        {
            var objects = await _fixture.Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
            var keys = objects.S3Objects?.Select(o => o.Key).ToList() ?? [];
            Assert.All(keys, k => Assert.DoesNotContain("BTC/USD", k));
            Assert.Contains(keys, k => k.Contains("BTC%2FUSD"));
        }

        // Fetch returns correct candles
        var fetched = new List<Candles.Candle>();
        await foreach (var c in repo.FetchCandlesAsync(symbol, interval, start, candles.Last().Timestamp))
            fetched.Add(c);
        Assert.Equal(candles.Count, fetched.Count);
        for (int i = 0; i < candles.Count; i++)
        {
            Assert.Equal(candles[i].Timestamp, fetched[i].Timestamp);
            Assert.Equal(candles[i].Open, fetched[i].Open);
        }

        // GetCandleFilesAsync and remove via returned CandleFileInfo
        var files = await repo.GetCandleFilesAsync(symbol, interval);
        Assert.NotEmpty(files);
        await repo.RemoveCandleFileAsync(files[0]);
        var filesAfter = await repo.GetCandleFilesAsync(symbol, interval);
        Assert.Empty(filesAfter);

        await CleanUpAfterTest(bucket);
    }
}

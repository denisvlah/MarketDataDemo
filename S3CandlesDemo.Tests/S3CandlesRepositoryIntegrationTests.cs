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
            var buckets = await testClient.ListBucketsAsync();
            ServiceUrl = $"http://localhost:{MinioPort}";
            Client = testClient;
            return;
        }
        catch { }

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
    }

    public async Task DisposeAsync()
    {
        if (Container != null)
        {
            await Container.CleanUpAsync();
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
        var repo = new S3CandlesDemo.Candles.S3CandlesRepository(bucket, prefix: null, client: _fixture.Client);
        string symbol = "GETREMOVE";
        int interval = 5;
        DateTime start = DateTime.UtcNow.Date.AddHours(8);
        var candles = Enumerable.Range(0, 5).Select(i => new S3CandlesDemo.Candles.Candle
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
        foreach (var obj in objects.S3Objects)
        {
            await _fixture.Client.DeleteObjectAsync(bucket, obj.Key);
        }
    }

    private async Task EnsureBucketAsync(string bucket)
    {
        if (_fixture.Client == null) throw new InvalidOperationException("S3 client is not initialized.");
        var buckets = await _fixture.Client.ListBucketsAsync();
        if (!buckets.Buckets.Any(b => b.BucketName == bucket))
        {
            await _fixture.Client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        }
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
        var repo = new S3CandlesDemo.Candles.S3CandlesRepository(bucket, prefix: null, client: _fixture.Client);
        string symbol = "MIDFILE";
        int interval = 10;
        DateTime start = DateTime.UtcNow.Date.AddHours(8);
        var candles = Enumerable.Range(0, 20).Select(i => new S3CandlesDemo.Candles.Candle
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
        var fetched = new List<S3CandlesDemo.Candles.Candle>();
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
        var repo = new S3CandlesDemo.Candles.S3CandlesRepository(bucket, prefix: null, client: _fixture.Client);
        string symbol = "MINIO2";
        int interval = 5;
        DateTime start = DateTime.UtcNow.Date.AddHours(9);
        var firstBatch = Enumerable.Range(0, 10).Select(i => new S3CandlesDemo.Candles.Candle
        {
            Timestamp = start.AddMinutes(i * interval),
            Open = i,
            High = i + 0.1,
            Low = i - 0.1,
            Close = i + 0.01,
            Volume = i * 10,
            TradeCount = i
        }).ToList();
        var secondBatch = Enumerable.Range(10, 10).Select(i => new S3CandlesDemo.Candles.Candle
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
        var fetched = new List<S3CandlesDemo.Candles.Candle>();
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
        var repo = new S3CandlesDemo.Candles.S3CandlesRepository(bucket, prefix: prefix, client: _fixture.Client);
        string symbol = "MINIOPFX";
        int interval = 15;
        DateTime start = DateTime.UtcNow.Date.AddHours(10);
        var candles = Enumerable.Range(0, 8).Select(i => new S3CandlesDemo.Candles.Candle
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
        var fetched = new List<S3CandlesDemo.Candles.Candle>();
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
        var repo = new S3CandlesDemo.Candles.S3CandlesRepository(bucket, prefix: null, client: _fixture.Client);
        string symbol = "MINIO";
        int interval = 5;
        DateTime start = DateTime.UtcNow.Date.AddHours(9);
        var candles = Enumerable.Range(0, 12).Select(i => new S3CandlesDemo.Candles.Candle
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
        var fetched = new List<S3CandlesDemo.Candles.Candle>();
        await foreach (var c in repo.FetchCandlesAsync(symbol, interval, candles.First().Timestamp, candles.Last().Timestamp))
            fetched.Add(c);
        Assert.Equal(candles.Count, fetched.Count);
        Assert.Equal(candles.First().Timestamp, fetched.First().Timestamp);
        Assert.Equal(candles.Last().Timestamp, fetched.Last().Timestamp);
        await CleanUpAfterTest(bucket);
    }
}

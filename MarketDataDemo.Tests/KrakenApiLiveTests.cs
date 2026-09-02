using Kraken.Net.Clients;
using Kraken.Net.Interfaces.Clients;
using Microsoft.Extensions.Logging;
using MarketDataDemo.Candles;
using MarketDataDemo.KrakenLatestCollector;
using Xunit.Abstractions;

namespace MarketDataDemo.Tests;

/// <summary>
/// Integration tests that call the real Kraken public API.
/// These tests verify that KrakenOhlcService works correctly against the live endpoint.
/// They are safe to run — the Kraken OHLC endpoint is public and unauthenticated.
/// Note: These tests depend on network access and Kraken availability.
/// </summary>
public class KrakenApiLiveTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IKrakenRestClient _restClient;
    private readonly KrakenOhlcService _service;

    public KrakenApiLiveTests(ITestOutputHelper output)
    {
        _output = output;
        _restClient = new KrakenRestClient();
        var loggerFactory = LoggerFactory.Create(b => b.AddXUnit(output));
        _service = new KrakenOhlcService(_restClient, loggerFactory.CreateLogger<KrakenOhlcService>());
    }

    public void Dispose()
    {
        _restClient.Dispose();
    }

    [Fact]
    public async Task GetOhlc_ReturnsCandles_ForBTCUSD()
    {
        var since = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var batch = await _service.GetOhlcAsync("XBTUSD", 60, since);

        Assert.NotNull(batch);
        Assert.NotEmpty(batch.Candles);
        // Kraken returns up to 720 entries; after discarding the uncommitted last candle we get up to 720
        Assert.True(batch.Candles.Count > 0 && batch.Candles.Count <= 720,
            $"Expected 1-720 candles, got {batch.Candles.Count}");

        // All candles should have valid OHLCV data
        foreach (var candle in batch.Candles)
        {
            Assert.True(candle.Timestamp >= since, $"Candle timestamp {candle.Timestamp} is before 'since' {since}");
            Assert.True(candle.Open > 0, "Open price should be positive");
            Assert.True(candle.High >= candle.Low, "High should be >= Low");
            Assert.True(candle.Volume >= 0, "Volume should be non-negative");
            Assert.True(candle.TradeCount >= 0, "TradeCount should be non-negative");
        }

        // Candles should be ordered by timestamp
        for (int i = 1; i < batch.Candles.Count; i++)
            Assert.True(batch.Candles[i].Timestamp > batch.Candles[i - 1].Timestamp,
                $"Candles not in order at index {i}");
    }

    [Fact]
    public async Task GetOhlc_ReturnsCandles_ForETHUSD()
    {
        var since = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var batch = await _service.GetOhlcAsync("ETHUSD", 1440, since);

        Assert.NotNull(batch);
        Assert.NotEmpty(batch.Candles);

        // Daily candles — should have reasonable count
        var firstCandle = batch.Candles.First();
        Assert.True(firstCandle.Timestamp >= since);
        Assert.True(firstCandle.Open > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(60)]
    [InlineData(1440)]
    public async Task GetOhlc_AllCommonIntervals_ReturnData(int intervalMinutes)
    {
        // Use a recent date so all intervals have data
        var since = DateTime.UtcNow.Date.AddDays(-3);
        var batch = await _service.GetOhlcAsync("XBTUSD", intervalMinutes, since);

        Assert.NotNull(batch);
        Assert.NotEmpty(batch.Candles);

        // Verify timestamps are spaced roughly by the interval
        if (batch.Candles.Count >= 2)
        {
            var gap = batch.Candles[1].Timestamp - batch.Candles[0].Timestamp;
            Assert.Equal(TimeSpan.FromMinutes(intervalMinutes), gap);
        }
    }

    [Fact]
    public async Task GetOhlc_InvalidPair_ThrowsAfterRetries()
    {
        // A completely invalid pair should fail after retries
        await Assert.ThrowsAsync<Exception>(async () =>
            await _service.GetOhlcAsync("INVALIDPAIR123", 60, DateTime.UtcNow.AddDays(-1)));
    }

    [Fact]
    public async Task GetOhlc_UnsupportedInterval_ThrowsArgumentException()
    {
        // Interval 7 is not in the IntervalMap
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.GetOhlcAsync("XBTUSD", 7, DateTime.UtcNow.AddDays(-1)));
    }

    [Fact]
    public async Task GetOhlc_Pagination_SecondBatchStartsAfterFirst()
    {
        // Fetch first batch from a historical date
        var since = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var firstBatch = await _service.GetOhlcAsync("XBTUSD", 60, since);

        Assert.NotEmpty(firstBatch.Candles);

        // Use the last candle's timestamp + interval as the next 'since'
        var lastTimestamp = firstBatch.Candles.Last().Timestamp;
        var nextSince = lastTimestamp.AddMinutes(60);

        var secondBatch = await _service.GetOhlcAsync("XBTUSD", 60, nextSince);

        Assert.NotEmpty(secondBatch.Candles);
        // Second batch should start at or after the next 'since'
        Assert.True(secondBatch.Candles.First().Timestamp >= nextSince,
            $"Second batch first candle {secondBatch.Candles.First().Timestamp} should be >= {nextSince}");

        // No overlap
        Assert.True(secondBatch.Candles.First().Timestamp > firstBatch.Candles.Last().Timestamp,
            "Second batch should not overlap with the first batch");
    }

    [Fact]
    public async Task GetOhlc_CandlesCanBeSerializedToBinary_RoundTrip()
    {
        var since = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var batch = await _service.GetOhlcAsync("XBTUSD", 60, since);

        Assert.NotEmpty(batch.Candles);

        // Verify every candle survives binary round-trip (the format used by ICandlesRepository)
        var buffer = new byte[Candle.CandleByteSize];
        foreach (var candle in batch.Candles)
        {
            Candle.CandleToBytes(candle, buffer);
            var roundTripped = Candle.BytesToCandle(buffer);

            Assert.Equal(candle.Timestamp, roundTripped.Timestamp);
            Assert.Equal(candle.Open, roundTripped.Open);
            Assert.Equal(candle.High, roundTripped.High);
            Assert.Equal(candle.Low, roundTripped.Low);
            Assert.Equal(candle.Close, roundTripped.Close);
            Assert.Equal(candle.Volume, roundTripped.Volume);
            Assert.Equal(candle.TradeCount, roundTripped.TradeCount);
        }
    }

    [Fact]
    public async Task GetOhlc_LastCandleDiscarded_TimestampNotCurrent()
    {
        // Fetch recent data — the last candle (current, uncommitted) should have been discarded
        var since = DateTime.UtcNow.Date.AddDays(-1);
        var batch = await _service.GetOhlcAsync("XBTUSD", 60, since);

        Assert.NotEmpty(batch.Candles);

        // The last returned candle should be at least 1 interval behind "now"
        var lastCandle = batch.Candles.Last();
        var minimumGap = TimeSpan.FromMinutes(60);
        Assert.True(DateTime.UtcNow - lastCandle.Timestamp >= minimumGap,
            $"Last candle at {lastCandle.Timestamp} is too recent — the current candle should have been discarded");
    }


    [Fact]
    public async Task GetOhlc_From_20240101_oneBatch()
    {
        // Fetch recent data — the last candle (current, uncommitted) should have been discarded
        var since = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var batch = await _service.GetOhlcAsync("XBTUSD", 1, since);
        _output.WriteLine("First candle timestamp: " + batch.Candles.First().Timestamp);
        _output.WriteLine("Last candle timestamp: " + batch.Candles.Last().Timestamp);
        _output.WriteLine("Total candles returned: " + batch.Candles.Count);


        Assert.NotEmpty(batch.Candles);        
    }
}

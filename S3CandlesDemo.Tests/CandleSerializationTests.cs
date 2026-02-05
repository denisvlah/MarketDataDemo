using S3CandlesDemo.Candles;
using System;

namespace S3CandlesDemo.Tests;

public class CandleSerializationTests
{

    [Fact]
    public void CandleToBytes_And_BytesToCandle_RoundTrip_Works()
    {
        var candle = new Candle
        {
            Timestamp = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Open = 100.5345,
            High = 110.0231,
            Low = 99.9442,
            Close = 105.2541,
            Volume = 12345.6712,
            TradeCount = 42
        };
        var buffer = new byte[Candle.CandleByteSize];
        Candle.CandleToBytes(candle, buffer);
        var deserialized = Candle.BytesToCandle(buffer);
        Assert.Equal(candle.Timestamp, deserialized.Timestamp);
        Assert.Equal(candle.Open, deserialized.Open);
        Assert.Equal(candle.High, deserialized.High);
        Assert.Equal(candle.Low, deserialized.Low);
        Assert.Equal(candle.Close, deserialized.Close);
        Assert.Equal(candle.Volume, deserialized.Volume);
        Assert.Equal(candle.TradeCount, deserialized.TradeCount);
    }

    [Fact]
    public void CandleByteSize_Is_Correct()
    {
        // Should match the sum of the field sizes
        int expected = sizeof(long) + sizeof(double) * 5 + sizeof(int);
        Assert.Equal(expected, Candle.CandleByteSize);
    }

    [Fact]
    public void CandleToBytes_Throws_On_Small_Buffer()
    {
        var candle = new Candle();
        var buffer = new byte[Candle.CandleByteSize - 1];
        Assert.Throws<ArgumentException>(() => Candle.CandleToBytes(candle, buffer));
    }

    [Fact]
    public void BytesToCandle_Throws_On_Small_Buffer()
    {
        var buffer = new byte[Candle.CandleByteSize - 1];
        Assert.Throws<ArgumentException>(() => Candle.BytesToCandle(buffer));
    }
}
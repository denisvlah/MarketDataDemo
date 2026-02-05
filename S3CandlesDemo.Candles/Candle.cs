using System;
using System.Text.Json.Serialization;

namespace S3CandlesDemo.Candles
{
    // Represents a single OHLCV candle as a struct
    public struct Candle
    {
        [JsonPropertyName("t")]
        public DateTime Timestamp { get; set; }
        
        [JsonPropertyName("o")]
        public double Open { get; set; }
        [JsonPropertyName("h")]
        public double High { get; set; }
        [JsonPropertyName("l")]
        public double Low { get; set; }
        [JsonPropertyName("c")]
        public double Close { get; set; }
        [JsonPropertyName("v")]
        public double Volume { get; set; }
        [JsonPropertyName("n")]
        public int TradeCount { get; set; }

        public static readonly int CandleByteSize = sizeof(long) + sizeof(double) * 5 + sizeof(int); // Timestamp, Open, High, Low, Close, Volume, TradeCount

        public static void CandleToBytes(Candle candle, Span<byte> buffer)
        {
            if (buffer.Length < CandleByteSize)
                throw new ArgumentException($"Buffer too small, must be at least {CandleByteSize} bytes", nameof(buffer));
            int offset = 0;
            BitConverter.TryWriteBytes(buffer.Slice(offset, sizeof(long)), candle.Timestamp.Ticks);
            offset += sizeof(long);
            BitConverter.TryWriteBytes(buffer.Slice(offset, sizeof(double)), candle.Open);
            offset += sizeof(double);
            BitConverter.TryWriteBytes(buffer.Slice(offset, sizeof(double)), candle.High);
            offset += sizeof(double);
            BitConverter.TryWriteBytes(buffer.Slice(offset, sizeof(double)), candle.Low);
            offset += sizeof(double);
            BitConverter.TryWriteBytes(buffer.Slice(offset, sizeof(double)), candle.Close);
            offset += sizeof(double);
            BitConverter.TryWriteBytes(buffer.Slice(offset, sizeof(double)), candle.Volume);
            offset += sizeof(double);
            BitConverter.TryWriteBytes(buffer.Slice(offset, sizeof(int)), candle.TradeCount);
        }

        public static Candle BytesToCandle(ReadOnlySpan<byte> arr)
        {
            if (arr.Length < CandleByteSize)
                throw new ArgumentException($"Buffer too small, must be at least {CandleByteSize} bytes", nameof(arr));
            int offset = 0;
            long ticks = BitConverter.ToInt64(arr.Slice(offset, sizeof(long)));
            offset += sizeof(long);
            double open = BitConverter.ToDouble(arr.Slice(offset, sizeof(double)));
            offset += sizeof(double);
            double high = BitConverter.ToDouble(arr.Slice(offset, sizeof(double)));
            offset += sizeof(double);
            double low = BitConverter.ToDouble(arr.Slice(offset, sizeof(double)));
            offset += sizeof(double);
            double close = BitConverter.ToDouble(arr.Slice(offset, sizeof(double)));
            offset += sizeof(double);
            double volume = BitConverter.ToDouble(arr.Slice(offset, sizeof(double)));
            offset += sizeof(double);
            int candleTradeCount = BitConverter.ToInt32(arr.Slice(offset, sizeof(int)));

            return new Candle
            {
                Timestamp = new DateTime(ticks, DateTimeKind.Utc),
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                TradeCount = candleTradeCount
            };
        }
    }
}

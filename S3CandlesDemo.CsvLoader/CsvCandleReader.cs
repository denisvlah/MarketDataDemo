using S3CandlesDemo.Candles;
using Sylvan.Data.Csv;

namespace S3CandlesDemo.CsvLoader;

/// <summary>
/// Reads OHLCV candle records from a CSV stream using Sylvan.Data.Csv for near-zero allocation streaming.
/// The stream is consumed one record at a time; it is never fully buffered in memory.
/// </summary>
public static class CsvCandleReader
{
    /// <summary>
    /// Parses candles from an already-opened <paramref name="stream"/> in timestamp order.
    /// The caller retains ownership of the stream and must dispose it after iteration.
    /// </summary>
    public static async IAsyncEnumerable<Candle> ReadCandlesAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);

        var opts = new CsvDataReaderOptions { HasHeaders = false };
        using var csvReader = await CsvDataReader.CreateAsync(reader, opts, ct);

        while (await csvReader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();

            int timestamp = csvReader.GetInt32(0);
            double open = csvReader.GetDouble(1);
            double high = csvReader.GetDouble(2);
            double low = csvReader.GetDouble(3);
            double close = csvReader.GetDouble(4);
            double volume = csvReader.GetDouble(5);
            int tradeCount = csvReader.GetInt32(6);

            yield return new Candle
            {
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                TradeCount = tradeCount
            };
        }
    }
}

using Amazon.S3;
using Amazon.S3.Model;
using S3CandlesDemo.Candles;
using Sylvan.Data.Csv;

namespace S3CandlesDemo.CsvLoader;

/// <summary>
/// Reads a CSV file from S3 and yields Candle records as an async enumerable.
/// Uses Sylvan.Data.Csv for near-zero allocation streaming.
/// </summary>
public static class CsvCandleReader
{
    /// <summary>
    /// Opens a CSV file from S3 and yields candles in timestamp order.
    /// The stream is never fully downloaded — records are parsed one at a time.
    /// </summary>
    public static async IAsyncEnumerable<Candle> ReadCandlesAsync(
        IAmazonS3 s3Client,
        string bucket,
        string key,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await s3Client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key
        }, ct);

        await using var s3Stream = response.ResponseStream;
        using var reader = new StreamReader(s3Stream);

        var opts = new CsvDataReaderOptions { HasHeaders = false };
        using var csvReader = CsvDataReader.Create(reader, opts);

        while (csvReader.Read())
        {
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

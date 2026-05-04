using S3CandlesDemo.Candles;

namespace S3CandlesDemo.Tests;

public class CsvConfigReaderTests
{
    [Fact]
    public void ParseLines_ValidInput_ReturnsConfigs()
    {
        var lines = new[]
        {
            "BTCUSD,XBTUSD,60,2024-01-01",
            "ETHUSD,ETHUSD,1440,2024-06-01",
            "SOLUSD,SOLUSD,15,2025-01-01"
        };

        var result = PairJobConfigReader.ParseLines(lines);

        Assert.Equal(3, result.Count);

        Assert.Equal("BTCUSD", result[0].AssetPair);
        Assert.Equal("XBTUSD", result[0].KrakenPair);
        Assert.Equal(60, result[0].IntervalMinutes);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), result[0].StartDate);

        Assert.Equal("ETHUSD", result[1].AssetPair);
        Assert.Equal("ETHUSD", result[1].KrakenPair);
        Assert.Equal(1440, result[1].IntervalMinutes);

        Assert.Equal("SOLUSD", result[2].AssetPair);
        Assert.Equal(15, result[2].IntervalMinutes);
    }

    [Fact]
    public void ParseLines_EmptyLines_AreSkipped()
    {
        var lines = new[] { "", "BTCUSD,XBTUSD,60,2024-01-01", "", "  " };
        var result = PairJobConfigReader.ParseLines(lines);
        Assert.Single(result);
        Assert.Equal("BTCUSD", result[0].AssetPair);
    }

    [Fact]
    public void ParseLines_WrongColumnCount_Throws()
    {
        var lines = new[] { "BTCUSD,XBTUSD,60" }; // missing start date
        var ex = Assert.Throws<FormatException>(() => PairJobConfigReader.ParseLines(lines));
        Assert.Contains("expected 4 columns", ex.Message);
    }

    [Fact]
    public void ParseLines_InvalidInterval_Throws()
    {
        var lines = new[] { "BTCUSD,XBTUSD,abc,2024-01-01" };
        var ex = Assert.Throws<FormatException>(() => PairJobConfigReader.ParseLines(lines));
        Assert.Contains("invalid interval", ex.Message);
    }

    [Fact]
    public void ParseLines_UnsupportedInterval_Throws()
    {
        var lines = new[] { "BTCUSD,XBTUSD,7,2024-01-01" }; // 7 is not a valid Kraken interval
        var ex = Assert.Throws<FormatException>(() => PairJobConfigReader.ParseLines(lines));
        Assert.Contains("not a valid Kraken interval", ex.Message);
    }

    [Fact]
    public void ParseLines_InvalidDate_Throws()
    {
        var lines = new[] { "BTCUSD,XBTUSD,60,not-a-date" };
        var ex = Assert.Throws<FormatException>(() => PairJobConfigReader.ParseLines(lines));
        Assert.Contains("invalid date", ex.Message);
    }

    [Fact]
    public void ParseLines_AllValidIntervals_Accepted()
    {
        var validIntervals = new[] { 1, 5, 15, 30, 60, 240, 1440, 10080, 21600 };
        foreach (var interval in validIntervals)
        {
            var lines = new[] { $"TEST,TEST,{interval},2024-01-01" };
            var result = PairJobConfigReader.ParseLines(lines);
            Assert.Single(result);
            Assert.Equal(interval, result[0].IntervalMinutes);
        }
    }

    [Fact]
    public void ParseLines_WhitespaceInValues_IsTrimmed()
    {
        var lines = new[] { "  BTCUSD , XBTUSD , 60 , 2024-01-01 " };
        var result = PairJobConfigReader.ParseLines(lines);
        Assert.Single(result);
        Assert.Equal("BTCUSD", result[0].AssetPair);
        Assert.Equal("XBTUSD", result[0].KrakenPair);
        Assert.Equal(60, result[0].IntervalMinutes);
    }

    [Fact]
    public void ReadFromFile_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => PairJobConfigReader.ReadFromFile("/nonexistent/path.csv"));
    }
}

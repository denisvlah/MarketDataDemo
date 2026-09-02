using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MarketDataDemo.Candles;

// Disambiguate 'Program' — use the Api project's Program class
using ApiProgram = MarketDataDemo.Api.Program;

namespace MarketDataDemo.Tests;

[Collection("Minio collection")]
public class WebApiS3IntegrationTests : IClassFixture<WebApplicationFactory<ApiProgram>>
{
    private readonly WebApplicationFactory<ApiProgram> _factory;
    private readonly MinioFixture _minio;
    private readonly HttpClient _client;

    public WebApiS3IntegrationTests(WebApplicationFactory<ApiProgram> factory, MinioFixture minio)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                var settings = new Dictionary<string, string>
                {
                    ["S3Candles:Bucket"] = "minio-test-bucket",
                    ["S3Candles:Prefix"] = "candles",
                    ["S3Candles:AWS:AccessKey"] = MinioFixture.AccessKey,
                    ["S3Candles:AWS:SecretKey"] = MinioFixture.SecretKey,
                    ["S3Candles:AWS:Region"] = "us-east-1",
                    ["S3Candles:AWS:Url"] = minio.ServiceUrl ?? "http://localhost:7000"
                };
                config.Add(new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
                {
                    InitialData = settings.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value))
                });
            });
        });
        _minio = minio;
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task StoreAndFetchCandles_WorksWithMinio()
    {
        // Arrange
        var symbol = "TESTAPI";
        var interval = 5;
        var candles = Enumerable.Range(0, 3).Select(i => new Candle
        {
            Timestamp = DateTime.UtcNow.AddMinutes(i * interval),
            Open = i,
            High = i + 0.5,
            Low = i - 0.5,
            Close = i + 0.1,
            Volume = i * 100,
            TradeCount = i
        }).ToList();

        // Act: Store candles
        var postResp = await _client.PostAsJsonAsync($"/candles/{symbol}/{interval}/bulk", candles);
        postResp.EnsureSuccessStatusCode();

        // Act: Fetch candles
        var from = candles.First().Timestamp;
        var to = candles.Last().Timestamp;
        var getResp = await _client.GetAsync($"/candles/{symbol}/{interval}?from={from:O}&to={to:O}");
        getResp.EnsureSuccessStatusCode();
        var fetched = await getResp.Content.ReadFromJsonAsync<List<Candle>>();

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal(candles.Count, fetched.Count);
        Assert.Equal(candles[0].Open, fetched[0].Open);
    }
}

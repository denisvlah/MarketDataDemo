using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MarketDataDemo.Candles;

// Disambiguate 'Program' — use the Api project's Program class
using ApiProgram = MarketDataDemo.Api.Program;

namespace MarketDataDemo.Tests;

public class WebApiFetchCandlesNoGaps
{
    private readonly HttpClient _client = new HttpClient
    {
        BaseAddress = new Uri("http://localhost:5044") // Ensure the API is running at this address
    };
    

   
    [Fact]
    public async Task Tes()
    {

        var symbol = "ETHEUR";
        var interval = 1;
        var from = new DateTime(2025, 1, 15);
        var to = new DateTime(2025, 2, 15);       


        // Act: Fetch candles
        var getResp = await _client.GetAsync($"/candles/{symbol}/{interval}?from={from:O}&to={to:O}");
        getResp.EnsureSuccessStatusCode();
        var fetched = await getResp.Content.ReadFromJsonAsync<List<Candle>>();
        Assert.NotNull(fetched);

        var dict = fetched.ToDictionary(c => c.Timestamp, c => c);
        for (var dt = from; dt < to; dt = dt.AddMinutes(interval))
        {
            Assert.True(dict.ContainsKey(dt), $"Missing candle for {dt:O}");
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.S3;
using S3CandlesDemo.Candles;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
var s3Config = builder.Configuration.GetSection("S3Candles");
var bucket = s3Config.GetValue<string>("Bucket");
var prefix = s3Config.GetValue<string>("Prefix");
var awsConfig = s3Config.GetSection("AWS");
var accessKey = awsConfig.GetValue<string>("AccessKey");
var secretKey = awsConfig.GetValue<string>("SecretKey");
var region = awsConfig.GetValue<string>("Region");
var url =  awsConfig.GetValue<string>("Url");

if (bucket == null || accessKey == null || secretKey == null || region == null)
{
    throw new Exception("S3Candles configuration is missing required fields.");
}

builder.Services.AddSingleton<ICandlesRepository>(sp => {
    AmazonS3Client s3Client;
    if (!string.IsNullOrWhiteSpace(url))
    {
        s3Client = new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
        {
            ServiceURL=url,
            ForcePathStyle = true,
            UseHttp = true
        });
    }
    else
    {
        s3Client = new AmazonS3Client(accessKey, secretKey, Amazon.RegionEndpoint.GetBySystemName(region));
    }
    
    return new S3CandlesRepository(bucket, prefix, s3Client);
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Store candles (bulk)
app.MapPost("/candles/{symbol}/{intervalMinutes}/bulk", async (string symbol, int intervalMinutes, List<Candle> candles, ICandlesRepository repo, CancellationToken cancellationToken) =>
{
    await repo.StoreCandlesAsync(symbol, intervalMinutes, candles, cancellationToken);
    return Results.Ok();
});

// Store candles (async stream, for advanced clients)
// Note: Minimal API does not natively support IAsyncEnumerable from body, so this is omitted for now.

// Fetch candles by symbol, time period, and interval
app.MapGet("/candles/{symbol}/{intervalMinutes}", async (string symbol, int intervalMinutes, DateTime from, DateTime to, ICandlesRepository repo, HttpContext ctx, CancellationToken cancellationToken) =>
    {
        var candles = repo.FetchCandlesAsync(symbol, intervalMinutes, from, to, cancellationToken);
        ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync("[");
    bool first = true;

    await foreach (var candle in candles)
    {
        if (!first) await ctx.Response.WriteAsync(",");
        first = false;

        await JsonSerializer.SerializeAsync(
            ctx.Response.Body,
            candle,
            AppJsonSerializerContext.Default.Candle,
            ctx.RequestAborted);
    }
    await ctx.Response.WriteAsync("]");
    });

// Retrieve all file info for a symbol/interval
app.MapGet("/candles/{symbol}/{intervalMinutes}/files", async (string symbol, int intervalMinutes, ICandlesRepository repo, CancellationToken cancellationToken) =>
    await repo.GetCandleFilesAsync(symbol, intervalMinutes, cancellationToken));

// Remove all files for a symbol/interval
app.MapDelete("/candles/{symbol}/{intervalMinutes}/files", async (string symbol, int intervalMinutes, ICandlesRepository repo, CancellationToken cancellationToken) =>
{
    await repo.RemoveCandleFilesAsync(symbol, intervalMinutes, cancellationToken);
    return Results.Ok();
});

// Remove a specific file by file info
app.MapDelete("/candles/file", async (CandleFileInfo fileInfo, ICandlesRepository repo, CancellationToken cancellationToken) =>
{
    await repo.RemoveCandleFileAsync(fileInfo, cancellationToken);
    return Results.Ok();
});

app.Run();

[JsonSerializable(typeof(Candle))]
[JsonSerializable(typeof(List<Candle>))]
[JsonSerializable(typeof(IReadOnlyList<CandleFileInfo>))]
[JsonSerializable(typeof(CandleFileInfo))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{

}
using Amazon.S3;
using S3CandlesDemo.Candles;
using S3CandlesDemo.CsvLoader;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// OpenAPI
builder.Services.AddOpenApi();

// Health checks
builder.Services.AddSingleton<GapFillingHealthCheck>();
builder.Services.AddHealthChecks()
    .AddCheck<GapFillingHealthCheck>("gap-filling");

// S3 client (shared between candle repository, CSV reads, and config reads)
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var s3Config = sp.GetRequiredService<IConfiguration>().GetSection("S3Candles");
    var awsConfig = s3Config.GetSection("AWS");
    var accessKey = awsConfig.GetValue<string>("AccessKey");
    var secretKey = awsConfig.GetValue<string>("SecretKey");
    var region = awsConfig.GetValue<string>("Region");
    var url = awsConfig.GetValue<string>("Url");

    if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey) || string.IsNullOrEmpty(region))
    {
        logger.LogCritical("S3 configuration is incomplete. AccessKey, SecretKey, and Region are required.");
        Environment.Exit(1);
    }

    if (!string.IsNullOrWhiteSpace(url))
        return new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
        {
            ServiceURL = url,
            ForcePathStyle = true,
            UseHttp = true
        });

    return new AmazonS3Client(accessKey, secretKey, Amazon.RegionEndpoint.GetBySystemName(region));
});

// S3 candle repository
builder.Services.AddSingleton<ICandlesRepository>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    try
    {
        var s3Config = sp.GetRequiredService<IConfiguration>().GetSection("S3Candles");
        var bucket = s3Config.GetValue<string>("Bucket");
        var prefix = s3Config.GetValue<string>("Prefix");

        if (string.IsNullOrEmpty(bucket))
        {
            logger.LogCritical("S3Candles:Bucket is required.");
            Environment.Exit(1);
        }

        var s3Client = sp.GetRequiredService<IAmazonS3>();
        return new S3CandlesRepository(bucket, prefix, s3Client);
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Failed to initialize S3CandlesRepository.");
        Environment.Exit(1);
        throw;
    }
});

// CSV source (S3-backed); swap with a filesystem implementation for tests
builder.Services.AddSingleton<ICsvSource>(sp =>
{
    var s3Config = sp.GetRequiredService<IConfiguration>().GetSection("S3Candles");
    var csvBucket = s3Config.GetValue<string>("CsvBucket") ?? "csv";
    var s3Client = sp.GetRequiredService<IAmazonS3>();
    var logger = sp.GetRequiredService<ILogger<S3CsvSource>>();
    return new S3CsvSource(s3Client, csvBucket, logger);
});

// Gap-filling background service
builder.Services.AddHostedService<GapFillingService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Health check endpoint
app.MapHealthChecks("/health");

app.Run();

namespace S3CandlesDemo.CsvLoader
{
    // Expose Program for test discoverability
    public partial class Program { }
}

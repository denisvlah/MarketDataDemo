using Amazon.S3;
using S3CandlesDemo.Candles;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// OpenAPI
builder.Services.AddOpenApi();

// Health checks
builder.Services.AddHealthChecks();

// S3 candle repository (same pattern as S3CandlesDemo.Api)
builder.Services.AddSingleton<ICandlesRepository>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    try
    {
        var s3Config = sp.GetRequiredService<IConfiguration>().GetSection("S3Candles");
        var bucket = s3Config.GetValue<string>("Bucket");
        var prefix = s3Config.GetValue<string>("Prefix");
        var awsConfig = s3Config.GetSection("AWS");
        var accessKey = awsConfig.GetValue<string>("AccessKey");
        var secretKey = awsConfig.GetValue<string>("SecretKey");
        var region = awsConfig.GetValue<string>("Region");
        var url = awsConfig.GetValue<string>("Url");

        if (string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(accessKey) ||
            string.IsNullOrEmpty(secretKey) || string.IsNullOrEmpty(region))
        {
            logger.LogCritical("S3 configuration is incomplete. Bucket, AccessKey, SecretKey, and Region are required.");
            Environment.Exit(1);
        }

        AmazonS3Client s3Client;
        if (!string.IsNullOrWhiteSpace(url))
            s3Client = new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
            {
                ServiceURL = url,
                ForcePathStyle = true,
                UseHttp = true
            });
        else
            s3Client = new AmazonS3Client(accessKey, secretKey, Amazon.RegionEndpoint.GetBySystemName(region));

        return new S3CandlesRepository(bucket, prefix, s3Client);
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Failed to initialize S3CandlesRepository.");
        Environment.Exit(1);
        throw;
    }
});

// HttpClient for Kraken API
builder.Services.AddHttpClient("Kraken", client =>
{
    client.BaseAddress = new Uri("https://api.kraken.com");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Health check endpoints
app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Redirect("/health"));

app.Run();

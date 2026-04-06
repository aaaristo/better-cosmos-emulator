using Cosmos.Emulator.Api.Endpoints;
using Cosmos.Emulator.Api.Middleware;
using Cosmos.Emulator.Core.Auth;
using Cosmos.Emulator.QueryEngine;
using Cosmos.Emulator.Storage;
using Cosmos.Emulator.Storage.Repositories;

var builder = WebApplication.CreateBuilder(args);

var config = builder.Configuration;
var masterKey = config.GetValue<string>("CosmosEmulator:MasterKey")
    ?? "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
var dataPath = config.GetValue<string>("CosmosEmulator:DataPath")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "data");

// Configure JSON to preserve property name casing (Cosmos API uses mixed casing)
// and use relaxed escaping so _etag quotes serialize as \" not \u0022
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

// Register services
builder.Services.AddSingleton(new SqliteStorageProvider(dataPath));
builder.Services.AddSingleton(new HmacSignatureValidator(masterKey));
builder.Services.AddTransient<DatabaseRepository>();
builder.Services.AddTransient<ContainerRepository>();
builder.Services.AddTransient<DocumentRepository>();
builder.Services.AddTransient<ChangeFeedRepository>();
builder.Services.AddSingleton<CosmosSqlQueryEngine>();

var app = builder.Build();

// Initialize storage
app.Services.GetRequiredService<SqliteStorageProvider>().Initialize();

// Global error handler — catch unhandled exceptions and return 500
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[ERROR] {context.Request.Method} {context.Request.Path}: {ex.Message}");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { code = "InternalServerError", message = ex.Message });
        }
    }
});

// Strip charset from Content-Type to match official emulator (application/json, not application/json; charset=utf-8)
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var ct = context.Response.ContentType;
        if (ct != null && ct.Contains("charset"))
            context.Response.ContentType = "application/json";
        return Task.CompletedTask;
    });
    await next();
});

// Middleware
app.UseMiddleware<VersionHeaderMiddleware>();
app.UseMiddleware<AuthenticationMiddleware>();

// Endpoints
app.MapAccountEndpoints();
app.MapDatabaseEndpoints();
app.MapContainerEndpoints();
app.MapDocumentEndpoints();
app.MapPartitionKeyRangeEndpoints();

app.Run();

public partial class Program { }

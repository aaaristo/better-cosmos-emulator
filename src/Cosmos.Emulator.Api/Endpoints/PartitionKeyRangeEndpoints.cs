using System.Text.Json;
using System.Text.Encodings.Web;
using Cosmos.Emulator.Storage.Repositories;

namespace Cosmos.Emulator.Api.Endpoints;

public static class PartitionKeyRangeEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = null,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void MapPartitionKeyRangeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dbs/{dbId}/colls/{collId}/pkranges", GetPartitionKeyRanges);
    }

    private static IResult GetPartitionKeyRanges(
        string dbId, string collId, HttpContext context,
        DatabaseRepository dbRepo, ContainerRepository containerRepo)
    {
        if (!dbRepo.Exists(dbId))
            return Results.Json(new { code = "NotFound", message = $"Database '{dbId}' not found." }, statusCode: 404);

        var container = containerRepo.Get(dbId, collId);
        if (container is null)
            return Results.Json(new { code = "NotFound", message = $"Container '{collId}' not found." }, statusCode: 404);

        var pkrangeRid = containerRepo.GetPkRangeRid(dbId, collId);
        var db = dbRepo.Get(dbId)!;

        // The SDK reads pkranges via change feed (A-IM: Incremental Feed).
        // It loops until it gets 304 Not Modified.
        // On first call (no If-None-Match or If-None-Match: *): return the ranges with an etag.
        // On subsequent calls (If-None-Match: <etag>): return 304.
        var ifNoneMatch = context.Request.Headers["If-None-Match"].FirstOrDefault();
        var isChangeFeed = context.Request.Headers["A-IM"].FirstOrDefault()?.Contains("Incremental") == true;

        if (isChangeFeed && ifNoneMatch != null && ifNoneMatch != "*")
        {
            // Subsequent call — no new changes. Return new etag so SDK doesn't think cache is stale.
            var newEtag = $"\"{Guid.NewGuid():N}\"";
            context.Response.Headers["etag"] = newEtag;
            context.Response.Headers["x-ms-item-count"] = "0";
            context.Response.Headers["x-ms-session-token"] = "0:-1#1";
            return Results.StatusCode(304);
        }

        // First call or non-change-feed request — return the full pkranges
        var etag = $"\"{Guid.NewGuid():N}\"";

        context.Response.Headers["x-ms-item-count"] = "1";
        context.Response.Headers["etag"] = etag;
        context.Response.Headers["x-ms-content-path"] = container.Rid;
        context.Response.Headers["x-ms-alt-content-path"] = $"dbs/{db.Id}/colls/{container.Id}";
        context.Response.Headers["lsn"] = "1";
        context.Response.Headers["x-ms-session-token"] = "0:-1#1";
        context.Response.Headers["x-ms-schemaversion"] = "1.19";
        context.Response.Headers["x-ms-global-committed-lsn"] = "0";

        var responseBody = new Dictionary<string, object>
        {
            ["_rid"] = container.Rid,
            ["PartitionKeyRanges"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["_rid"] = pkrangeRid!,
                    ["id"] = "0",
                    ["_etag"] = container.Etag,
                    ["minInclusive"] = "",
                    ["maxExclusive"] = "FF",
                    ["ridPrefix"] = 0,
                    ["_self"] = $"dbs/{db.Rid}/colls/{container.Rid}/pkranges/{pkrangeRid}/",
                    ["throughputFraction"] = 1,
                    ["status"] = "online",
                    ["parents"] = Array.Empty<string>(),
                    ["ownedArchivalPKRangeIds"] = Array.Empty<string>(),
                    ["_ts"] = container.Ts,
                    ["lsn"] = 1
                }
            },
            ["_count"] = 1
        };

        return Results.Json(responseBody, JsonOpts);
    }
}

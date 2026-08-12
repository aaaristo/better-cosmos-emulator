using System.Text.Json;
using Cosmos.Emulator.Core.Helpers;
using Cosmos.Emulator.Core.Models;
using Cosmos.Emulator.Storage.Repositories;

namespace Cosmos.Emulator.Api.Endpoints;

public static class ContainerEndpoints
{
    public static void MapContainerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dbs/{dbId}/colls", CreateContainer);
        app.MapGet("/dbs/{dbId}/colls", ListContainers);
        app.MapGet("/dbs/{dbId}/colls/{collId}", GetContainer);
        app.MapPut("/dbs/{dbId}/colls/{collId}", ReplaceContainer);
        app.MapDelete("/dbs/{dbId}/colls/{collId}", DeleteContainer);
    }

    private static IResult CreateContainer(string dbId, HttpContext context,
        DatabaseRepository dbRepo, ContainerRepository containerRepo)
    {
        if (!dbRepo.Exists(dbId))
            return Results.Json(new { code = "NotFound", message = $"Database '{dbId}' not found." }, statusCode: 404);

        var body = ReadBody(context).GetAwaiter().GetResult();

        if (!body.TryGetProperty("id", out var idProp))
            return Results.Json(new { code = "BadRequest", message = "Missing 'id' property." }, statusCode: 400);

        var id = idProp.GetString()!;

        if (containerRepo.Exists(dbId, id))
            return Results.Json(new { code = "Conflict", message = $"Entity with the specified id already exists in the system. id = {id}" }, statusCode: 409);

        // Parse partition key
        PartitionKeyDefinition partitionKey;
        if (body.TryGetProperty("partitionKey", out var pkProp))
        {
            try
            {
                partitionKey = JsonSerializer.Deserialize<PartitionKeyDefinition>(pkProp.GetRawText())!;
            }
            catch (JsonException)
            {
                // Notably a 'partitionKey' with no 'paths' at all, which would otherwise
                // surface as a 500 from the required-member check.
                return Results.Json(new { code = "BadRequest", message = "The 'partitionKey' property is malformed. It must specify 'paths'." }, statusCode: 400);
            }
        }
        else
        {
            return Results.Json(new { code = "BadRequest", message = "Missing 'partitionKey' property." }, statusCode: 400);
        }

        var partitionKeyError = ValidatePartitionKeyPaths(partitionKey);
        if (partitionKeyError is not null)
            return Results.Json(new { code = "BadRequest", message = partitionKeyError }, statusCode: 400);

        // Parse indexing policy (optional, use defaults)
        IndexingPolicy indexingPolicy;
        if (body.TryGetProperty("indexingPolicy", out var idxProp))
        {
            indexingPolicy = JsonSerializer.Deserialize<IndexingPolicy>(idxProp.GetRawText())!;
        }
        else
        {
            indexingPolicy = new IndexingPolicy();
        }

        // Parse default TTL (optional)
        int? defaultTtl = null;
        if (body.TryGetProperty("defaultTtl", out var ttlProp))
        {
            defaultTtl = ttlProp.GetInt32();
        }

        var dbRid = dbRepo.Get(dbId)!.Rid;
        var rid = ResourceIdGenerator.GenerateCollectionRid(dbRid);
        var pkrangeRid = ResourceIdGenerator.GeneratePkRangeRid(rid);

        var container = new CosmosContainer
        {
            Id = id,
            Rid = rid,
            Self = $"dbs/{dbRid}/colls/{rid}/",
            Etag = EtagGenerator.Generate(),
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            PartitionKey = partitionKey,
            IndexingPolicy = indexingPolicy,
            DefaultTtl = defaultTtl
        };

        containerRepo.Create(dbId, container, pkrangeRid);

        context.Response.Headers["etag"] = container.Etag;
        return Results.Json(container, statusCode: 201);
    }

    private static IResult ListContainers(string dbId, HttpContext context,
        DatabaseRepository dbRepo, ContainerRepository containerRepo)
    {
        if (!dbRepo.Exists(dbId))
            return Results.Json(new { code = "NotFound", message = $"Database '{dbId}' not found." }, statusCode: 404);

        var containers = PaginationHelper.Apply(context, containerRepo.List(dbId));
        return Results.Json(new
        {
            _rid = dbRepo.Get(dbId)!.Rid,
            DocumentCollections = containers,
            _count = containers.Count
        });
    }

    private static IResult GetContainer(string dbId, string collId, HttpContext context,
        DatabaseRepository dbRepo, ContainerRepository containerRepo)
    {
        if (!dbRepo.Exists(dbId))
            return Results.Json(new { code = "NotFound", message = $"Database '{dbId}' not found." }, statusCode: 404);

        var container = containerRepo.Get(dbId, collId);
        if (container is null)
            return Results.Json(new { code = "NotFound", message = $"Entity with the specified id does not exist in the system. id = {collId}" }, statusCode: 404);

        context.Response.Headers["etag"] = container.Etag;
        return Results.Json(container);
    }

    private static IResult ReplaceContainer(string dbId, string collId, HttpContext context,
        DatabaseRepository dbRepo, ContainerRepository containerRepo)
    {
        if (!dbRepo.Exists(dbId))
            return Results.Json(new { code = "NotFound", message = $"Database '{dbId}' not found." }, statusCode: 404);

        var existing = containerRepo.Get(dbId, collId);
        if (existing is null)
            return Results.Json(new { code = "NotFound", message = $"Entity with the specified id does not exist in the system. id = {collId}" }, statusCode: 404);

        var body = ReadBody(context).GetAwaiter().GetResult();

        // Update indexing policy if provided
        if (body.TryGetProperty("indexingPolicy", out var idxProp))
        {
            existing.IndexingPolicy = JsonSerializer.Deserialize<IndexingPolicy>(idxProp.GetRawText())!;
        }

        if (body.TryGetProperty("defaultTtl", out var ttlProp))
        {
            existing.DefaultTtl = ttlProp.GetInt32();
        }

        existing.Etag = EtagGenerator.Generate();
        existing.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        containerRepo.Replace(dbId, existing);

        context.Response.Headers["etag"] = existing.Etag;
        return Results.Json(existing);
    }

    private static IResult DeleteContainer(string dbId, string collId,
        DatabaseRepository dbRepo, ContainerRepository containerRepo)
    {
        if (!dbRepo.Exists(dbId))
            return Results.Json(new { code = "NotFound", message = $"Database '{dbId}' not found." }, statusCode: 404);

        if (!containerRepo.Exists(dbId, collId))
            return Results.Json(new { code = "NotFound", message = $"Entity with the specified id does not exist in the system. id = {collId}" }, statusCode: 404);

        containerRepo.Delete(dbId, collId);
        return Results.StatusCode(204);
    }

    /// <summary>
    /// Cosmos partitions a container on one path, or on two or three for a hierarchical
    /// (sub-partitioned) container. Accepting more would produce a container the real
    /// service could never host, and accepting none would silently funnel every document
    /// into a single partition key of '[]'.
    /// </summary>
    private const int MaxPartitionKeyPaths = 3;

    /// <returns>An error message, or null when the definition is acceptable.</returns>
    private static string? ValidatePartitionKeyPaths(PartitionKeyDefinition partitionKey)
    {
        if (partitionKey.Paths.Count == 0)
            return "The 'partitionKey' property must specify at least one path.";

        if (partitionKey.Paths.Count > MaxPartitionKeyPaths)
            return $"Too many partition key paths ({partitionKey.Paths.Count}) specified. A maximum of {MaxPartitionKeyPaths} is allowed.";

        return null;
    }

    private static async Task<JsonElement> ReadBody(HttpContext context)
    {
        using var doc = await JsonDocument.ParseAsync(context.Request.Body);
        return doc.RootElement.Clone();
    }
}

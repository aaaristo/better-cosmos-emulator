using System.Text.Json;
using Cosmos.Emulator.Core.Helpers;
using Cosmos.Emulator.Core.Models;
using Cosmos.Emulator.Storage.Repositories;

namespace Cosmos.Emulator.Api.Endpoints;

public static class DatabaseEndpoints
{
    public static void MapDatabaseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dbs", CreateDatabase);
        app.MapGet("/dbs", ListDatabases);
        app.MapGet("/dbs/{dbId}", GetDatabase);
        app.MapDelete("/dbs/{dbId}", DeleteDatabase);
    }

    private static IResult CreateDatabase(HttpContext context, DatabaseRepository repo)
    {
        var body = ReadBody(context).GetAwaiter().GetResult();
        if (!body.TryGetProperty("id", out var idProp))
            return Results.Json(new { code = "BadRequest", message = "Missing 'id' property." }, statusCode: 400);

        var id = idProp.GetString()!;

        if (repo.Exists(id))
            return Results.Json(new { code = "Conflict", message = $"Entity with the specified id already exists in the system. id = {id}" }, statusCode: 409);

        var rid = ResourceIdGenerator.GenerateDatabaseRid();
        var database = new CosmosDatabase
        {
            Id = id,
            Rid = rid,
            Self = $"dbs/{rid}/",
            Etag = EtagGenerator.Generate(),
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        repo.Create(database);

        context.Response.Headers["etag"] = database.Etag;
        return Results.Json(database, statusCode: 201);
    }

    private static IResult ListDatabases(HttpContext context, DatabaseRepository repo)
    {
        var databases = PaginationHelper.Apply(context, repo.List());
        return Results.Json(new
        {
            _rid = "",
            Databases = databases,
            _count = databases.Count
        });
    }

    private static IResult GetDatabase(string dbId, DatabaseRepository repo)
    {
        var database = repo.Get(dbId);
        if (database is null)
            return Results.Json(new { code = "NotFound", message = $"Entity with the specified id does not exist in the system. id = {dbId}" }, statusCode: 404);

        return Results.Json(database);
    }

    private static IResult DeleteDatabase(string dbId, DatabaseRepository repo)
    {
        if (!repo.Exists(dbId))
            return Results.Json(new { code = "NotFound", message = $"Entity with the specified id does not exist in the system. id = {dbId}" }, statusCode: 404);

        repo.Delete(dbId);
        return Results.StatusCode(204);
    }

    private static async Task<JsonElement> ReadBody(HttpContext context)
    {
        using var doc = await JsonDocument.ParseAsync(context.Request.Body);
        return doc.RootElement.Clone();
    }
}

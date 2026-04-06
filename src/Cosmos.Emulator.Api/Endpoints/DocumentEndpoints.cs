using System.Text.Json;
using Cosmos.Emulator.Core.Helpers;
using Cosmos.Emulator.Core.Models;
using Cosmos.Emulator.QueryEngine;
using Cosmos.Emulator.Storage.Repositories;

namespace Cosmos.Emulator.Api.Endpoints;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dbs/{dbId}/colls/{collId}/docs", HandleDocumentPost);
        app.MapGet("/dbs/{dbId}/colls/{collId}/docs", HandleDocumentList);
        app.MapGet("/dbs/{dbId}/colls/{collId}/docs/{docId}", GetDocument);
        app.MapPut("/dbs/{dbId}/colls/{collId}/docs/{docId}", ReplaceDocument);
        app.MapPatch("/dbs/{dbId}/colls/{collId}/docs/{docId}", PatchDocument);
        app.MapDelete("/dbs/{dbId}/colls/{collId}/docs/{docId}", DeleteDocument);
    }

    private static async Task<IResult> HandleDocumentPost(
        string dbId, string collId, HttpContext context,
        DatabaseRepository dbRepo, ContainerRepository containerRepo, DocumentRepository docRepo,
        CosmosSqlQueryEngine queryEngine)
    {
        if (!dbRepo.Exists(dbId))
            return Results.Json(new { code = "NotFound", message = $"Database '{dbId}' not found." }, statusCode: 404);

        var container = containerRepo.Get(dbId, collId);
        if (container is null)
            return Results.Json(new { code = "NotFound", message = $"Container '{collId}' not found." }, statusCode: 404);

        // Query plan request: the SDK asks "how should I execute this query?"
        // We return a simple plan telling the SDK to execute it as a single-partition passthrough.
        var isQueryPlanRequest = context.Request.Headers["x-ms-cosmos-is-query-plan-request"]
            .FirstOrDefault()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true;
        if (isQueryPlanRequest)
        {
            return await HandleQueryPlanRequest(context, container);
        }

        var contentType = context.Request.ContentType ?? "";
        var isQuery = contentType.Contains("application/query+json", StringComparison.OrdinalIgnoreCase)
                      || context.Request.Headers["x-ms-documentdb-isquery"].FirstOrDefault()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true;

        if (isQuery)
        {
            return await HandleQuery(dbId, collId, container, context, docRepo, queryEngine);
        }

        return await CreateDocument(dbId, collId, container, context, docRepo, containerRepo);
    }

    private static async Task<IResult> CreateDocument(
        string dbId, string collId, CosmosContainer container,
        HttpContext context, DocumentRepository docRepo, ContainerRepository containerRepo)
    {
        var body = await ReadBody(context);
        var isUpsert = context.Request.Headers["x-ms-documentdb-is-upsert"].FirstOrDefault()
            ?.Equals("True", StringComparison.OrdinalIgnoreCase) == true;

        if (!body.TryGetProperty("id", out var idProp))
            return Results.Json(new { code = "BadRequest", message = "Missing 'id' property." }, statusCode: 400);

        var docId = idProp.GetString()!;
        var partitionKey = PartitionKeyExtractor.Extract(body, container.PartitionKey.Paths);

        // Check for existing document
        var existing = docRepo.Get(dbId, collId, docId, partitionKey);
        if (existing is not null && !isUpsert)
        {
            return Results.Json(new { code = "Conflict", message = $"Entity with the specified id already exists in the system. id = {docId}" }, statusCode: 409);
        }

        var rid = existing?.Rid ?? ResourceIdGenerator.GenerateDocumentRid(container.Rid);
        var etag = EtagGenerator.Generate();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var enrichedBody = SystemPropertyInjector.Inject(
            body, rid,
            $"dbs/{container.Self.Split('/')[1]}/colls/{container.Rid}/docs/{rid}/",
            etag, ts, "attachments/");

        var document = new CosmosDocument
        {
            Id = docId,
            Rid = rid,
            PartitionKey = partitionKey,
            Body = enrichedBody,
            Etag = etag,
            Ts = ts
        };

        if (existing is not null)
        {
            docRepo.Replace(dbId, collId, document);
        }
        else
        {
            docRepo.Create(dbId, collId, document);
        }

        context.Response.Headers["etag"] = etag;
        return Results.Json(enrichedBody, statusCode: existing is not null ? 200 : 201);
    }

    private static async Task<IResult> HandleQuery(
        string dbId, string collId, CosmosContainer container,
        HttpContext context, DocumentRepository docRepo, CosmosSqlQueryEngine queryEngine)
    {
        var body = await ReadBody(context);
        var queryText = body.GetProperty("query").GetString()!;

        // Parse user-supplied parameters
        Dictionary<string, object>? userParams = null;
        if (body.TryGetProperty("parameters", out var paramsProp) && paramsProp.ValueKind == JsonValueKind.Array)
        {
            userParams = new Dictionary<string, object>();
            foreach (var p in paramsProp.EnumerateArray())
            {
                var name = p.GetProperty("name").GetString()!;
                var val = p.GetProperty("value");
                userParams[name] = val.ValueKind switch
                {
                    JsonValueKind.String => val.GetString()!,
                    JsonValueKind.Number when val.TryGetInt64(out var l) => l,
                    JsonValueKind.Number => val.GetDouble(),
                    JsonValueKind.True => 1L,
                    JsonValueKind.False => 0L,
                    _ => val.GetRawText()
                };
            }
        }

        string? partitionKey = null;
        if (context.Request.Headers.TryGetValue("x-ms-documentdb-partitionkey", out var pkHeader))
        {
            partitionKey = PartitionKeyExtractor.FromHeader(pkHeader.FirstOrDefault()!);
        }

        try
        {
            // All queries — including SDK-rewritten ones with JSON object/array literals —
            // go through the Cosmos SQL parser natively.
            var knownColumns = docRepo.GetKnownColumns(dbId, collId);
            var translated = queryEngine.Translate(queryText, collId, knownColumns, userParams);
            var sql = translated.Sql;
            var parameters = translated.Parameters;

            if (partitionKey is not null)
            {
                sql = sql.Replace(
                    "WHERE is_deleted = 0",
                    $"WHERE is_deleted = 0 AND partition_key = @__pk");
                parameters["@__pk"] = partitionKey;
            }

            // Apply max item count and continuation
            var maxItems = 100;
            if (context.Request.Headers.TryGetValue("x-ms-max-item-count", out var maxItemsHeader))
            {
                if (int.TryParse(maxItemsHeader.FirstOrDefault(), out var parsed) && parsed > 0)
                    maxItems = parsed;
            }

            var queryOffset = 0;
            var queryContinuation = context.Request.Headers["x-ms-continuation"].FirstOrDefault();
            if (queryContinuation is not null)
            {
                try
                {
                    var tokenBytes = Convert.FromBase64String(queryContinuation);
                    var tokenJson = JsonDocument.Parse(tokenBytes);
                    queryOffset = tokenJson.RootElement.GetProperty("offset").GetInt32();
                }
                catch { }
            }

            if (!sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                sql += $" LIMIT {maxItems}";
                if (queryOffset > 0)
                    sql += $" OFFSET {queryOffset}";
            }

            var results = docRepo.ExecuteQuery(dbId, collId, sql, parameters);

            context.Response.Headers["x-ms-item-count"] = results.Count.ToString();

            if (results.Count == maxItems)
            {
                var nextToken = Convert.ToBase64String(
                    JsonSerializer.SerializeToUtf8Bytes(new { offset = queryOffset + maxItems }));
                context.Response.Headers["x-ms-continuation"] = nextToken;
            }

            return Results.Json(new
            {
                _rid = container.Rid,
                Documents = results,
                _count = results.Count
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new
            {
                code = "BadRequest",
                message = $"Failed to execute query: {ex.Message}"
            }, statusCode: 400);
        }
    }

    private static IResult HandleDocumentList(
        string dbId, string collId, HttpContext context,
        DatabaseRepository dbRepo, ContainerRepository containerRepo,
        DocumentRepository docRepo, ChangeFeedRepository cfRepo)
    {
        if (!dbRepo.Exists(dbId))
            return Results.Json(new { code = "NotFound", message = $"Database '{dbId}' not found." }, statusCode: 404);

        var container = containerRepo.Get(dbId, collId);
        if (container is null)
            return Results.Json(new { code = "NotFound", message = $"Container '{collId}' not found." }, statusCode: 404);

        // Check for change feed request
        // LatestVersion uses "A-IM: Incremental Feed"
        // AllVersionsAndDeletes uses "A-IM: Full-Fidelity Feed"
        var aim = context.Request.Headers["A-IM"].FirstOrDefault();
        if (aim is not null && (aim.Contains("Incremental", StringComparison.OrdinalIgnoreCase)
                             || aim.Contains("Full-Fidelity", StringComparison.OrdinalIgnoreCase)))
        {
            return HandleChangeFeed(dbId, collId, container, context, docRepo, cfRepo);
        }

        // Regular list
        var maxItems = 100;
        if (context.Request.Headers.TryGetValue("x-ms-max-item-count", out var maxItemsHeader))
        {
            int.TryParse(maxItemsHeader.FirstOrDefault(), out maxItems);
        }

        string? partitionKey = null;
        if (context.Request.Headers.TryGetValue("x-ms-documentdb-partitionkey", out var pkHeader))
        {
            partitionKey = PartitionKeyExtractor.FromHeader(pkHeader.FirstOrDefault()!);
        }

        var continuation = context.Request.Headers["x-ms-continuation"].FirstOrDefault();

        var docs = docRepo.List(dbId, collId, partitionKey, maxItems, continuation);
        var bodies = docs.Select(d => d.Body).ToList();

        context.Response.Headers["x-ms-item-count"] = bodies.Count.ToString();

        // Generate continuation if we got a full page
        if (docs.Count == maxItems)
        {
            long nextOffset = 0;
            if (continuation is not null)
            {
                var tokenJson = JsonDocument.Parse(Convert.FromBase64String(continuation));
                nextOffset = tokenJson.RootElement.GetProperty("offset").GetInt64();
            }
            nextOffset += maxItems;

            var newToken = Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(new { offset = nextOffset }));
            context.Response.Headers["x-ms-continuation"] = newToken;
        }

        return Results.Json(new
        {
            _rid = container.Rid,
            Documents = bodies,
            _count = bodies.Count
        });
    }

    private static IResult HandleChangeFeed(
        string dbId, string collId, CosmosContainer container,
        HttpContext context, DocumentRepository docRepo, ChangeFeedRepository cfRepo)
    {
        var maxItems = 100;
        if (context.Request.Headers.TryGetValue("x-ms-max-item-count", out var maxItemsHeader))
        {
            int.TryParse(maxItemsHeader.FirstOrDefault(), out maxItems);
        }

        string? partitionKey = null;
        if (context.Request.Headers.TryGetValue("x-ms-documentdb-partitionkey", out var pkHeader))
        {
            partitionKey = PartitionKeyExtractor.FromHeader(pkHeader.FirstOrDefault()!);
        }

        var changeFeedMode = context.Request.Headers["x-ms-cosmos-changefeed-mode"].FirstOrDefault();
        var aimHeader = context.Request.Headers["A-IM"].FirstOrDefault() ?? "";
        var isAllVersions = changeFeedMode?.Equals("AllVersionsAndDeletes", StringComparison.OrdinalIgnoreCase) == true
                         || aimHeader.Contains("Full-Fidelity", StringComparison.OrdinalIgnoreCase);

        // Parse continuation token from If-None-Match header
        long afterLsn = 0;
        var ifNoneMatch = context.Request.Headers["If-None-Match"].FirstOrDefault();
        if (ifNoneMatch is not null && ifNoneMatch != "*")
        {
            try
            {
                var tokenBytes = Convert.FromBase64String(ifNoneMatch.Trim('"'));
                var tokenJson = JsonDocument.Parse(tokenBytes);
                afterLsn = tokenJson.RootElement.GetProperty("lsn").GetInt64();
            }
            catch
            {
                // If we can't parse, start from beginning
            }
        }

        if (isAllVersions)
        {
            var entries = cfRepo.ReadAllVersionsAndDeletes(dbId, collId, partitionKey, afterLsn, maxItems);

            if (entries.Count == 0)
            {
                var currentLsn = cfRepo.GetCurrentLsn(dbId, collId);
                var emptyToken = CreateContinuationToken(currentLsn, true);
                context.Response.Headers["etag"] = $"\"{emptyToken}\"";
                context.Response.Headers["x-ms-item-count"] = "0";
                return Results.StatusCode(304);
            }

            var maxLsn = entries.Max(e => e.Lsn);
            var token = CreateContinuationToken(maxLsn, true);
            context.Response.Headers["etag"] = $"\"{token}\"";
            context.Response.Headers["x-ms-item-count"] = entries.Count.ToString();

            var items = entries.Select(e => new AllVersionsChangeFeedItem
            {
                Current = e.Body,
                Metadata = new ChangeFeedMetadata
                {
                    OperationType = e.Operation,
                    Lsn = e.Lsn,
                    Crts = e.Ts,
                    TimeToLiveExpired = false
                }
            }).ToList();

            return Results.Json(new
            {
                _rid = container.Rid,
                Documents = items,
                _count = items.Count
            });
        }
        else
        {
            // LatestVersion mode
            var docs = cfRepo.ReadLatestVersion(dbId, collId, partitionKey, afterLsn, maxItems);

            if (docs.Count == 0)
            {
                var currentLsn = cfRepo.GetCurrentLsn(dbId, collId);
                var emptyToken = CreateContinuationToken(currentLsn, false);
                context.Response.Headers["etag"] = $"\"{emptyToken}\"";
                context.Response.Headers["x-ms-item-count"] = "0";
                return Results.StatusCode(304);
            }

            var maxLsn = docs.Max(d => d.Lsn);
            var token = CreateContinuationToken(maxLsn, false);
            context.Response.Headers["etag"] = $"\"{token}\"";
            context.Response.Headers["x-ms-item-count"] = docs.Count.ToString();

            var bodies = docs.Select(d => d.Body).ToList();

            return Results.Json(new
            {
                _rid = container.Rid,
                Documents = bodies,
                _count = bodies.Count
            });
        }
    }

    private static IResult GetDocument(
        string dbId, string collId, string docId, HttpContext context,
        DatabaseRepository dbRepo, ContainerRepository containerRepo, DocumentRepository docRepo)
    {
        if (!dbRepo.Exists(dbId))
            return Results.Json(new { code = "NotFound", message = $"Database '{dbId}' not found." }, statusCode: 404);
        if (!containerRepo.Exists(dbId, collId))
            return Results.Json(new { code = "NotFound", message = $"Container '{collId}' not found." }, statusCode: 404);

        var pkHeader = context.Request.Headers["x-ms-documentdb-partitionkey"].FirstOrDefault();
        if (pkHeader is null)
            return Results.Json(new { code = "BadRequest", message = "Missing x-ms-documentdb-partitionkey header." }, statusCode: 400);

        var partitionKey = PartitionKeyExtractor.FromHeader(pkHeader);
        var doc = docRepo.Get(dbId, collId, docId, partitionKey);

        if (doc is null)
            return Results.Json(new { code = "NotFound", message = $"Entity with the specified id does not exist in the system. id = {docId}" }, statusCode: 404);

        context.Response.Headers["etag"] = doc.Etag;
        return Results.Json(doc.Body);
    }

    private static async Task<IResult> ReplaceDocument(
        string dbId, string collId, string docId, HttpContext context,
        DatabaseRepository dbRepo, ContainerRepository containerRepo, DocumentRepository docRepo)
    {
        if (!dbRepo.Exists(dbId))
            return Results.Json(new { code = "NotFound", message = $"Database '{dbId}' not found." }, statusCode: 404);

        var container = containerRepo.Get(dbId, collId);
        if (container is null)
            return Results.Json(new { code = "NotFound", message = $"Container '{collId}' not found." }, statusCode: 404);

        var pkHeader = context.Request.Headers["x-ms-documentdb-partitionkey"].FirstOrDefault();
        if (pkHeader is null)
            return Results.Json(new { code = "BadRequest", message = "Missing x-ms-documentdb-partitionkey header." }, statusCode: 400);

        var partitionKey = PartitionKeyExtractor.FromHeader(pkHeader);

        var existing = docRepo.Get(dbId, collId, docId, partitionKey);
        if (existing is null)
            return Results.Json(new { code = "NotFound", message = $"Entity with the specified id does not exist in the system. id = {docId}" }, statusCode: 404);

        // Check etag precondition
        var ifMatch = context.Request.Headers["If-Match"].FirstOrDefault();
        if (ifMatch is not null && ifMatch != existing.Etag)
            return Results.Json(new { code = "PreconditionFailed", message = "The operation specified an eTag that is different from the version available at the server." }, statusCode: 412);

        var body = await ReadBody(context);
        var etag = EtagGenerator.Generate();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var enrichedBody = SystemPropertyInjector.Inject(
            body, existing.Rid,
            $"dbs/{container.Self.Split('/')[1]}/colls/{container.Rid}/docs/{existing.Rid}/",
            etag, ts, "attachments/");

        var document = new CosmosDocument
        {
            Id = docId,
            Rid = existing.Rid,
            PartitionKey = partitionKey,
            Body = enrichedBody,
            Etag = etag,
            Ts = ts
        };

        docRepo.Replace(dbId, collId, document);

        context.Response.Headers["etag"] = etag;
        return Results.Json(enrichedBody);
    }

    private static async Task<IResult> PatchDocument(
        string dbId, string collId, string docId, HttpContext context,
        DatabaseRepository dbRepo, ContainerRepository containerRepo, DocumentRepository docRepo)
    {
        if (!dbRepo.Exists(dbId))
            return Results.Json(new { code = "NotFound", message = $"Database '{dbId}' not found." }, statusCode: 404);

        var container = containerRepo.Get(dbId, collId);
        if (container is null)
            return Results.Json(new { code = "NotFound", message = $"Container '{collId}' not found." }, statusCode: 404);

        var pkHeader = context.Request.Headers["x-ms-documentdb-partitionkey"].FirstOrDefault();
        if (pkHeader is null)
            return Results.Json(new { code = "BadRequest", message = "Missing x-ms-documentdb-partitionkey header." }, statusCode: 400);

        var partitionKey = PartitionKeyExtractor.FromHeader(pkHeader);

        var existing = docRepo.Get(dbId, collId, docId, partitionKey);
        if (existing is null)
            return Results.Json(new { code = "NotFound", message = $"Entity with the specified id does not exist in the system. id = {docId}" }, statusCode: 404);

        // Check etag precondition
        var ifMatch = context.Request.Headers["If-Match"].FirstOrDefault();
        if (ifMatch is not null && ifMatch != existing.Etag)
            return Results.Json(new { code = "PreconditionFailed", message = "The operation specified an eTag that is different from the version available at the server." }, statusCode: 412);

        var patchBody = await ReadBody(context);

        if (!patchBody.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
            return Results.Json(new { code = "BadRequest", message = "Missing 'operations' array in patch body." }, statusCode: 400);

        // Apply patch operations to the existing document body
        var node = System.Text.Json.Nodes.JsonNode.Parse(existing.Body.GetRawText())!.AsObject();

        foreach (var op in operations.EnumerateArray())
        {
            var opType = op.GetProperty("op").GetString()!.ToLowerInvariant();
            var path = op.GetProperty("path").GetString()!;
            var segments = path.TrimStart('/').Split('/');

            switch (opType)
            {
                case "add":
                case "set":
                {
                    var value = op.TryGetProperty("value", out var v)
                        ? System.Text.Json.Nodes.JsonNode.Parse(v.GetRawText())
                        : null;
                    SetNestedValue(node, segments, value);
                    break;
                }
                case "replace":
                {
                    var value = op.TryGetProperty("value", out var v)
                        ? System.Text.Json.Nodes.JsonNode.Parse(v.GetRawText())
                        : null;
                    if (GetNestedValue(node, segments) is null)
                        return Results.Json(new { code = "BadRequest", message = $"Path '{path}' does not exist for replace." }, statusCode: 400);
                    SetNestedValue(node, segments, value);
                    break;
                }
                case "remove":
                {
                    if (!RemoveNestedValue(node, segments))
                        return Results.Json(new { code = "BadRequest", message = $"Path '{path}' does not exist for remove." }, statusCode: 400);
                    break;
                }
                case "incr":
                case "increment":
                {
                    var incrValue = op.GetProperty("value").GetDouble();
                    var current = GetNestedValue(node, segments);
                    double currentNum = 0;
                    if (current is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<double>(out var d))
                        currentNum = d;
                    SetNestedValue(node, segments, System.Text.Json.Nodes.JsonValue.Create(currentNum + incrValue));
                    break;
                }
                default:
                    return Results.Json(new { code = "BadRequest", message = $"Unsupported patch operation: '{opType}'." }, statusCode: 400);
            }
        }

        var etag = EtagGenerator.Generate();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Update system properties
        node["_etag"] = etag;
        node["_ts"] = ts;

        var patchedBody = JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

        var document = new CosmosDocument
        {
            Id = docId,
            Rid = existing.Rid,
            PartitionKey = partitionKey,
            Body = patchedBody,
            Etag = etag,
            Ts = ts
        };

        docRepo.Replace(dbId, collId, document);

        context.Response.Headers["etag"] = etag;
        return Results.Json(patchedBody);
    }

    private static void SetNestedValue(System.Text.Json.Nodes.JsonObject root, string[] segments, System.Text.Json.Nodes.JsonNode? value)
    {
        var current = (System.Text.Json.Nodes.JsonNode)root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (current is System.Text.Json.Nodes.JsonObject obj)
            {
                if (!obj.ContainsKey(segments[i]))
                    obj[segments[i]] = new System.Text.Json.Nodes.JsonObject();
                current = obj[segments[i]]!;
            }
            else if (current is System.Text.Json.Nodes.JsonArray arr && int.TryParse(segments[i], out var idx))
            {
                current = arr[idx]!;
            }
        }

        var lastSegment = segments[^1];
        if (current is System.Text.Json.Nodes.JsonObject parentObj)
        {
            parentObj[lastSegment] = value;
        }
        else if (current is System.Text.Json.Nodes.JsonArray parentArr)
        {
            if (lastSegment == "-")
                parentArr.Add(value);
            else if (int.TryParse(lastSegment, out var idx))
                parentArr[idx] = value;
        }
    }

    private static System.Text.Json.Nodes.JsonNode? GetNestedValue(System.Text.Json.Nodes.JsonObject root, string[] segments)
    {
        System.Text.Json.Nodes.JsonNode? current = root;
        foreach (var segment in segments)
        {
            if (current is System.Text.Json.Nodes.JsonObject obj && obj.ContainsKey(segment))
                current = obj[segment];
            else if (current is System.Text.Json.Nodes.JsonArray arr && int.TryParse(segment, out var idx) && idx < arr.Count)
                current = arr[idx];
            else
                return null;
        }
        return current;
    }

    private static bool RemoveNestedValue(System.Text.Json.Nodes.JsonObject root, string[] segments)
    {
        if (segments.Length == 1)
            return root.Remove(segments[0]);

        var parent = GetNestedValue(root, segments[..^1]);
        var lastSegment = segments[^1];

        if (parent is System.Text.Json.Nodes.JsonObject parentObj)
            return parentObj.Remove(lastSegment);
        if (parent is System.Text.Json.Nodes.JsonArray parentArr && int.TryParse(lastSegment, out var idx))
        {
            parentArr.RemoveAt(idx);
            return true;
        }
        return false;
    }

    private static IResult DeleteDocument(
        string dbId, string collId, string docId, HttpContext context,
        DatabaseRepository dbRepo, ContainerRepository containerRepo, DocumentRepository docRepo)
    {
        if (!dbRepo.Exists(dbId))
            return Results.Json(new { code = "NotFound", message = $"Database '{dbId}' not found." }, statusCode: 404);
        if (!containerRepo.Exists(dbId, collId))
            return Results.Json(new { code = "NotFound", message = $"Container '{collId}' not found." }, statusCode: 404);

        var pkHeader = context.Request.Headers["x-ms-documentdb-partitionkey"].FirstOrDefault();
        if (pkHeader is null)
            return Results.Json(new { code = "BadRequest", message = "Missing x-ms-documentdb-partitionkey header." }, statusCode: 400);

        var partitionKey = PartitionKeyExtractor.FromHeader(pkHeader);

        if (!docRepo.Exists(dbId, collId, docId, partitionKey))
            return Results.Json(new { code = "NotFound", message = $"Entity with the specified id does not exist in the system. id = {docId}" }, statusCode: 404);

        docRepo.Delete(dbId, collId, docId, partitionKey);
        return Results.StatusCode(204);
    }

    private static string CreateContinuationToken(long lsn, bool allVersions)
    {
        var obj = allVersions
            ? new { lsn, mode = "AllVersionsAndDeletes" }
            : (object)new { lsn };
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(obj));
    }

    /* Legacy methods removed — see git history.
    private static (string query, List<string>? orderByFields) _removed_RewriteSdkQuery(string query)
    {
        // Pattern 1: ORDER BY with orderByItems/payload
        if (query.Contains("orderByItems", StringComparison.OrdinalIgnoreCase))
        {
            var orderByFields = new List<string>();
            var itemMatches = System.Text.RegularExpressions.Regex.Matches(
                query, @"AS orderByItems.*?""item"":\s*\w+\.(\w+(?:\.\w+)*)");
            foreach (System.Text.RegularExpressions.Match m in itemMatches)
                orderByFields.Add(m.Groups[1].Value);

            var topMatch = System.Text.RegularExpressions.Regex.Match(
                query, @"SELECT\s+(TOP\s+\d+)\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var topClause = topMatch.Success ? topMatch.Groups[1].Value + " " : "";

            // Check if payload is a custom projection (not just "c AS payload")
            // Match ", {key: expr} AS payload" but NOT ", c AS payload"
            // Look for the pattern where the char before AS payload is "}" (custom) vs a word char (simple)
            var hasCustomPayload = System.Text.RegularExpressions.Regex.IsMatch(
                query, @"\}\s+AS\s+payload\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var customPayloadMatch = hasCustomPayload
                ? System.Text.RegularExpressions.Regex.Match(
                    query, @",\s*(\{[^{}]*(?:\([^)]*\))*[^{}]*\})\s+AS\s+payload",
                    System.Text.RegularExpressions.RegexOptions.Singleline)
                : System.Text.RegularExpressions.Match.Empty;

            var fromMatch = System.Text.RegularExpressions.Regex.Match(
                query, @"(FROM\s+.+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

            if (customPayloadMatch.Success && fromMatch.Success)
            {
                // Custom payload like {"displayName": (c.nickname ?? c.name)}
                var payloadRaw = customPayloadMatch.Groups[1].Value;
                // Strip outer braces
                var payloadContent = payloadRaw.TrimStart('{').TrimEnd('}');
                var payloadParts = new List<string>();

                // Match "key": (expr) or "key": c.field
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                    payloadContent, @"""(\w+)"":\s*(.+?)(?=,\s*""|$)"))
                {
                    var key = m.Groups[1].Value;
                    var expr = m.Groups[2].Value.Trim();
                    // Replace c.field references with column names
                    expr = System.Text.RegularExpressions.Regex.Replace(expr, @"\bc\.(\w+)", m2 => $"[{m2.Groups[1].Value}]");
                    // Handle ?? coalesce → COALESCE
                    expr = System.Text.RegularExpressions.Regex.Replace(expr, @"\((.+?)\s*\?\?\s*(.+?)\)", "COALESCE($1, $2)");
                    payloadParts.Add($"'{key}', {expr}");
                }

                if (payloadParts.Count > 0)
                {
                    // Build: json_object('_rid', _rid, 'orderByItems', [...], 'payload', json_object(...))
                    var obItemParts = orderByFields.Select(f => $"json_object('item', [{f}])").ToList();
                    var obArray = $"json_array({string.Join(", ", obItemParts)})";
                    var payloadObj = $"json_object({string.Join(", ", payloadParts)})";
                    var selectExpr = $"json_object('_rid', rid, 'orderByItems', {obArray}, 'payload', {payloadObj})";

                    var rest = NormalizeFromClause(fromMatch.Groups[1].Value)
                        .Replace("WHERE (true)\n", "").Replace("WHERE (true)", "").Trim();
                    // This is raw SQL — no orderByFields wrapping needed
                    return ($"SELECT {topClause}{selectExpr} {rest}", null);
                }
            }

            if (fromMatch.Success)
            {
                // Simple payload (c AS payload) — return full documents + wrap later
                var rest = fromMatch.Groups[1].Value
                    .Replace("WHERE (true)\n", "").Replace("WHERE (true)", "").Trim();
                return ($"SELECT {topClause}* {rest}", orderByFields);
            }

            return (query, orderByFields);
        }

        // Pattern 1b: GROUP BY with groupByItems/payload
        // SELECT [{"item": c.city}] AS groupByItems, {"city": c.city, "cnt": {"item": COUNT(1)}} AS payload
        // FROM c GROUP BY c.city
        //
        // The SDK expects each result row to have "groupByItems" and "payload" fields.
        if (query.Contains("groupByItems", StringComparison.OrdinalIgnoreCase))
        {
            // Extract group-by fields from [{"item": c.field}] AS groupByItems
            var groupByFields = new List<string>();
            var gbItemMatches = System.Text.RegularExpressions.Regex.Matches(
                query, @"""item"":\s*\w+\.(\w+).*?AS\s+groupByItems");
            foreach (System.Text.RegularExpressions.Match m in gbItemMatches)
                groupByFields.Add(m.Groups[1].Value);

            // Extract payload fields
            var payloadMatch = System.Text.RegularExpressions.Regex.Match(query, @",\s*\{(.+)\}\s+AS\s+payload", System.Text.RegularExpressions.RegexOptions.Singleline);
            var fromMatch = System.Text.RegularExpressions.Regex.Match(query, @"(FROM\s+.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

            if (payloadMatch.Success && fromMatch.Success)
            {
                var payloadContent = payloadMatch.Groups[1].Value;
                var payloadParts = new List<string>();

                // Match "key": c.field (non-aggregate)
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(payloadContent, @"""(\w+)"":\s*\w+\.(\w+)(?!\s*\()"))
                    payloadParts.Add($"'{m.Groups[1].Value}', [{m.Groups[2].Value}]");

                // Match "key": {"item": AGG(expr)} — keep the {"item": value} wrapper
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(payloadContent, @"""(\w+)"":\s*\{""item"":\s*(\w+\([^)]*\))\}"))
                    payloadParts.Add($"'{m.Groups[1].Value}', json_object('item', {m.Groups[2].Value})");

                if (payloadParts.Count > 0)
                {
                    // Build groupByItems array: [{"item": value}] for each group field
                    var gbItemParts = groupByFields.Select(f => $"json_object('item', [{f}])").ToList();
                    var gbArray = $"json_array({string.Join(", ", gbItemParts)})";

                    var payloadObj = $"json_object({string.Join(", ", payloadParts)})";

                    // Build the full row: {"groupByItems": [...], "payload": {...}}
                    var selectExpr = $"json_object('groupByItems', {gbArray}, 'payload', {payloadObj})";

                    var rest = NormalizeFromClause(fromMatch.Groups[1].Value)
                        .Replace("WHERE (true)\n", "").Replace("WHERE (true)", "").Trim();
                    return ($"SELECT {selectExpr} {rest}", null);
                }
            }
        }

        // Pattern 2: Aggregate with [{ ... }] wrapper
        // The SDK wraps aggregates in SELECT VALUE [{...}] with various formats:
        //   SELECT VALUE [{"item": COUNT(1)}] FROM c
        //   SELECT VALUE [{"item": SUM(c.age)}] FROM c
        //   SELECT VALUE [{"item": {"sum": SUM(c.age), "count": COUNT(c.age)}}] FROM c
        //   SELECT VALUE [{"item": MIN(c.age), "item2": {"min": MIN(c.age), "count": COUNT(c.age)}}] FROM c
        //
        // Strategy: extract the full JSON-like object between [{ and }], parse all
        // key-value pairs, and build a json_array(json_object(...)) SQL expression.
        var aggMatch = System.Text.RegularExpressions.Regex.Match(
            query, @"SELECT\s+VALUE\s+\[\{(.+)\}\]\s*(FROM\s+.+)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

        if (aggMatch.Success)
        {
            var innerContent = aggMatch.Groups[1].Value.Trim();
            var fromClause = aggMatch.Groups[2].Success ? aggMatch.Groups[2].Value.Trim() : "";

            // Extract all top-level "key": value pairs from the JSON-like object
            // Handle both simple values (COUNT(1)) and nested objects ({"sum": SUM(c.age), ...})
            var jsonParts = new List<string>();
            var pairMatches = System.Text.RegularExpressions.Regex.Matches(
                innerContent, @"""(\w+)"":\s*(\{[^}]+\}|\w+\([^)]*\))");
            foreach (System.Text.RegularExpressions.Match pm in pairMatches)
            {
                var key = pm.Groups[1].Value;
                var val = pm.Groups[2].Value;

                if (val.StartsWith("{"))
                {
                    // Nested object — extract inner key:value pairs
                    var nestedParts = new List<string>();
                    var nestedMatches = System.Text.RegularExpressions.Regex.Matches(
                        val, @"""(\w+)"":\s*(\w+\([^)]*\))");
                    foreach (System.Text.RegularExpressions.Match nm in nestedMatches)
                        nestedParts.Add($"'{nm.Groups[1].Value}', {nm.Groups[2].Value}");
                    jsonParts.Add($"'{key}', json_object({string.Join(", ", nestedParts)})");
                }
                else
                {
                    jsonParts.Add($"'{key}', {val}");
                }
            }

            if (jsonParts.Count > 0)
            {
                var selectExpr = $"json_array(json_object({string.Join(", ", jsonParts)}))";
                if (string.IsNullOrEmpty(fromClause))
                    fromClause = "FROM [__table__]";
                else
                    fromClause = NormalizeFromClause(fromClause);
                return ($"SELECT {selectExpr} {fromClause}"
                    .Replace("WHERE (true)\n", "").Replace("WHERE (true)", "").Trim(), null);
            }
        }

        return (query, null);
    }

    /// <summary>
    /// Replaces FROM alias (e.g., "FROM c") with "FROM [__table__]" placeholder.
    /// The actual table name is substituted later.
    /// </summary>
    private static string NormalizeFromClause(string fromClause)
    {
        // Replace "FROM c" or "FROM c\n" with "FROM [__table__]"
        return System.Text.RegularExpressions.Regex.Replace(
            fromClause, @"FROM\s+(\w+)",
            "FROM [__table__]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Wraps query results in the SDK's expected orderByItems + payload format.
    /// </summary>
    private static List<JsonElement> WrapOrderByResults(List<JsonElement> results, List<string> orderByFields)
    {
        var wrapped = new List<JsonElement>();
        foreach (var doc in results)
        {
            var orderByItems = new List<Dictionary<string, object?>>();
            foreach (var field in orderByFields)
            {
                object? value = null;
                if (doc.TryGetProperty(field, out var prop))
                {
                    value = prop.ValueKind switch
                    {
                        JsonValueKind.String => prop.GetString(),
                        JsonValueKind.Number when prop.TryGetInt64(out var l) => l,
                        JsonValueKind.Number => prop.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null
                    };
                }
                orderByItems.Add(new Dictionary<string, object?> { ["item"] = value });
            }

            string? rid = null;
            if (doc.TryGetProperty("_rid", out var ridProp))
                rid = ridProp.GetString();

            var wrappedDoc = new Dictionary<string, object?>
            {
                ["_rid"] = rid,
                ["orderByItems"] = orderByItems,
                ["payload"] = doc
            };

            var json = JsonSerializer.Serialize(wrappedDoc, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            wrapped.Add(JsonDocument.Parse(json).RootElement.Clone());
        }
        return wrapped;
    }
    */

    /// <summary>
    /// Handles query plan requests (x-ms-cosmos-is-query-plan-request: True).
    /// The SDK sends these before executing queries to determine the execution strategy.
    /// We return a plan that tells the SDK to execute as a passthrough single-partition query.
    /// </summary>
    private static async Task<IResult> HandleQueryPlanRequest(HttpContext context, CosmosContainer container)
    {
        var body = await ReadBody(context);
        var queryText = body.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";

        // Determine if the query has ORDER BY, GROUP BY, aggregates, DISTINCT, TOP, OFFSET
        var upperQuery = queryText.ToUpperInvariant();
        var hasOrderBy = upperQuery.Contains("ORDER BY");
        var hasGroupBy = upperQuery.Contains("GROUP BY");
        var hasAggregate = upperQuery.Contains("COUNT(") || upperQuery.Contains("SUM(")
                        || upperQuery.Contains("AVG(") || upperQuery.Contains("MIN(") || upperQuery.Contains("MAX(");
        var hasDistinct = upperQuery.Contains("DISTINCT");
        var hasTop = upperQuery.Contains("TOP ");
        var hasOffset = upperQuery.Contains("OFFSET");

        // Build the query plan response
        var queryInfo = new Dictionary<string, object?>
        {
            ["distinctType"] = hasDistinct ? "Ordered" : "None",
            ["top"] = null,
            ["offset"] = null,
            ["limit"] = null,
            ["orderBy"] = hasOrderBy ? new[] { "Ascending" } : Array.Empty<string>(),
            ["orderByExpressions"] = hasOrderBy ? new[] { queryText } : Array.Empty<string>(),
            ["groupByExpressions"] = Array.Empty<string>(),
            ["groupByAliases"] = Array.Empty<string>(),
            ["aggregates"] = hasAggregate ? new[] { "Count" } : Array.Empty<string>(),
            ["groupByAliasToAggregateType"] = new Dictionary<string, string>(),
            ["rewrittenQuery"] = queryText,
            ["hasSelectValue"] = upperQuery.Contains("SELECT VALUE"),
        };

        var plan = new Dictionary<string, object>
        {
            ["partitionedQueryExecutionInfoVersion"] = 2,
            ["queryInfo"] = queryInfo,
            ["queryRanges"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["min"] = "",
                    ["max"] = "FF",
                    ["isMinInclusive"] = true,
                    ["isMaxInclusive"] = false
                }
            }
        };

        return Results.Json(plan);
    }

    private static async Task<JsonElement> ReadBody(HttpContext context)
    {
        using var doc = await JsonDocument.ParseAsync(context.Request.Body);
        return doc.RootElement.Clone();
    }
}

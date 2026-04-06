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

        var contentType = context.Request.ContentType ?? "";
        var isQuery = contentType.Contains("application/query+json", StringComparison.OrdinalIgnoreCase)
                      && context.Request.Headers["x-ms-documentdb-isquery"].FirstOrDefault()?.Equals("True", StringComparison.OrdinalIgnoreCase) == true;

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

        // The SDK rewrites queries (ORDER BY, aggregates) with internal wrapper formats.
        // We detect and simplify them to standard Cosmos SQL.
        var (rewrittenQuery, orderByFields) = RewriteSdkQuery(queryText);
        queryText = rewrittenQuery;

        // Replace table placeholder for aggregate queries that had no FROM clause
        var quotedTable = $"[{collId.Replace("]", "]]")}]";
        queryText = queryText.Replace("[__table__]", quotedTable);

        // For raw SQL aggregates only, replace c.field references with column names
        // The SDK sends e.g. SUM(c.age) — in SQLite this needs to be SUM([age])
        var isRawSqlQuery = queryText.StartsWith("SELECT json_quote(", StringComparison.OrdinalIgnoreCase)
                         || queryText.StartsWith("SELECT json_object(", StringComparison.OrdinalIgnoreCase)
                         || queryText.StartsWith("SELECT json_array(", StringComparison.OrdinalIgnoreCase);
        if (isRawSqlQuery)
        {
            queryText = System.Text.RegularExpressions.Regex.Replace(
                queryText, @"\bc\.(\w+)", m => $"[{m.Groups[1].Value}]");
        }

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

        // Add partition key filter if provided
        string? partitionKey = null;
        if (context.Request.Headers.TryGetValue("x-ms-documentdb-partitionkey", out var pkHeader))
        {
            partitionKey = PartitionKeyExtractor.FromHeader(pkHeader.FirstOrDefault()!);
        }

        try
        {
            string sql;
            var parameters = userParams ?? new Dictionary<string, object>();

            // Check if the query was rewritten to raw SQL (aggregates bypass Cosmos SQL parser)
            var isRawSql = queryText.StartsWith("SELECT json_quote(", StringComparison.OrdinalIgnoreCase)
                        || queryText.StartsWith("SELECT json_object(", StringComparison.OrdinalIgnoreCase)
                        || queryText.StartsWith("SELECT json_array(", StringComparison.OrdinalIgnoreCase);

            if (isRawSql)
            {
                // Already raw SQLite SQL — add WHERE clause for partition key and is_deleted
                sql = queryText;
                var table = $"[{collId.Replace("]", "]]")}]";

                // Add WHERE is_deleted = 0 filter
                if (sql.Contains("FROM", StringComparison.OrdinalIgnoreCase))
                {
                    var fromIdx = sql.IndexOf("FROM", StringComparison.OrdinalIgnoreCase);
                    var afterFrom = sql.IndexOf(' ', fromIdx + 5); // skip "FROM [table]"
                    if (afterFrom == -1) afterFrom = sql.Length;

                    var whereClause = " WHERE is_deleted = 0";
                    if (partitionKey is not null)
                    {
                        whereClause += " AND partition_key = @__pk";
                        parameters["@__pk"] = partitionKey;
                    }
                    sql = sql[..afterFrom] + whereClause + sql[afterFrom..];
                }
            }
            else
            {
                var knownColumns = docRepo.GetKnownColumns(dbId, collId);
                var translated = queryEngine.Translate(queryText, collId, knownColumns, userParams);
                sql = translated.Sql;
                parameters = translated.Parameters;

                if (partitionKey is not null)
                {
                    sql = sql.Replace(
                        "WHERE is_deleted = 0",
                        $"WHERE is_deleted = 0 AND partition_key = @__pk");
                    parameters["@__pk"] = partitionKey;
                }
            }

            // Apply max item count as LIMIT if not already present
            var maxItems = 100;
            if (context.Request.Headers.TryGetValue("x-ms-max-item-count", out var maxItemsHeader))
            {
                int.TryParse(maxItemsHeader.FirstOrDefault(), out maxItems);
            }
            if (!isRawSql && !sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                sql += $" LIMIT {maxItems}";
            }

            var results = docRepo.ExecuteQuery(dbId, collId, sql, parameters);

            // Wrap results for SDK ORDER BY queries
            if (orderByFields is not null)
                results = WrapOrderByResults(results, orderByFields);

            context.Response.Headers["x-ms-item-count"] = results.Count.ToString();

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

    /// <summary>
    /// The SDK rewrites queries to include [{"item": expr}] wrappers:
    ///
    /// ORDER BY: SELECT [TOP n] c._rid, [{"item": c.field}] AS orderByItems, c AS payload FROM c ORDER BY ...
    /// Aggregates: SELECT VALUE [{"item": COUNT(1)}]  (no FROM clause)
    /// Complex agg: SELECT VALUE [{"item": {"sum": SUM(c.age), "count": COUNT(c.age)}}]
    ///
    /// We detect these patterns, simplify to standard Cosmos SQL, and wrap results as needed.
    /// </summary>
    private static (string query, List<string>? orderByFields) RewriteSdkQuery(string query)
    {
        // Pattern 1: ORDER BY with orderByItems/payload
        if (query.Contains("orderByItems", StringComparison.OrdinalIgnoreCase))
        {
            var orderByFields = new List<string>();
            var itemMatches = System.Text.RegularExpressions.Regex.Matches(
                query, @"\{""item"":\s*(\w+)\.(\w+(?:\.\w+)*)\}");
            foreach (System.Text.RegularExpressions.Match m in itemMatches)
                orderByFields.Add(m.Groups[2].Value);

            var topMatch = System.Text.RegularExpressions.Regex.Match(
                query, @"SELECT\s+(TOP\s+\d+)\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var topClause = topMatch.Success ? topMatch.Groups[1].Value + " " : "";

            var fromMatch = System.Text.RegularExpressions.Regex.Match(
                query, @"(FROM\s+.+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

            if (fromMatch.Success)
            {
                var rest = fromMatch.Groups[1].Value
                    .Replace("WHERE (true)\n", "").Replace("WHERE (true)", "").Trim();
                return ($"SELECT {topClause}* {rest}", orderByFields);
            }

            return (query, orderByFields);
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

    private static async Task<JsonElement> ReadBody(HttpContext context)
    {
        using var doc = await JsonDocument.ParseAsync(context.Request.Body);
        return doc.RootElement.Clone();
    }
}

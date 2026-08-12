using System.Collections.Concurrent;
using System.Text.Json;
using Cosmos.Emulator.Core.Helpers;
using Cosmos.Emulator.Core.Models;
using Cosmos.Emulator.Storage.Schema;
using Microsoft.Data.Sqlite;

namespace Cosmos.Emulator.Storage.Repositories;

public class DocumentRepository
{
    private readonly SqliteStorageProvider _storage;

    // Cache of known columns per (database, container) to avoid repeated PRAGMA calls
    private static readonly ConcurrentDictionary<string, HashSet<string>> ColumnCache = new();

    private static readonly HashSet<string> SystemJsonProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "_rid", "_self", "_etag", "_ts", "_attachments"
    };

    // SQLite system columns — user properties matching these (case-insensitive) must not
    // become dynamic columns because SQLite column names are case-insensitive.
    private static readonly HashSet<string> SystemSqliteColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "rid", "partition_key", "body", "etag", "ts", "is_deleted", "lsn"
    };

    public DocumentRepository(SqliteStorageProvider storage)
    {
        _storage = storage;
    }

    public CosmosDocument? Get(string databaseId, string containerId, string documentId, string partitionKey)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);
        var table = Q(containerId);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, rid, partition_key, body, etag, ts, is_deleted, lsn
            FROM {table}
            WHERE id = @id AND partition_key = @pk AND is_deleted = 0
            """;
        cmd.Parameters.AddWithValue("@id", documentId);
        cmd.Parameters.AddWithValue("@pk", partitionKey);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return ReadDocument(reader);
    }

    public List<CosmosDocument> List(string databaseId, string containerId, string? partitionKey = null, int maxItems = 100, string? continuationToken = null, EpkRange? epkRange = null)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);
        var table = Q(containerId);

        using var cmd = conn.CreateCommand();
        var where = "is_deleted = 0";
        if (partitionKey is not null)
        {
            // A hierarchical container may be listed by a prefix of its key components.
            where += " AND " + PartitionKeyPredicate.BuildSql("partition_key", "@pk");
            BindPartitionKey(cmd, "@pk", partitionKey);
        }
        if (epkRange is { } epk)
        {
            where += " AND " + EpkFilter.BuildSql("partition_key", "@epk");
            EpkFilter.Bind(cmd, "@epk", epk);
        }

        long offset = 0;
        if (continuationToken is not null)
        {
            using var tokenJson = JsonDocument.Parse(Convert.FromBase64String(continuationToken));
            offset = tokenJson.RootElement.GetProperty("offset").GetInt64();
        }

        cmd.CommandText = $"""
            SELECT id, rid, partition_key, body, etag, ts, is_deleted, lsn
            FROM {table}
            WHERE {where}
            ORDER BY rid
            LIMIT @limit OFFSET @offset
            """;
        cmd.Parameters.AddWithValue("@limit", maxItems);
        cmd.Parameters.AddWithValue("@offset", offset);

        var result = new List<CosmosDocument>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadDocument(reader));
        }
        return result;
    }

    public (CosmosDocument doc, long newLsn) Create(string databaseId, string containerId, CosmosDocument document)
        => CreateAsync(databaseId, containerId, document).GetAwaiter().GetResult();

    public Task<(CosmosDocument doc, long newLsn)> CreateAsync(string databaseId, string containerId, CosmosDocument document)
    {
        return _storage.ExecuteWriteAsync(databaseId, conn =>
        {
            var newLsn = IncrementLsn(conn, containerId);
            document.Lsn = newLsn;

            var dynamicCols = EnsureColumnsAndExtract(conn, databaseId, containerId, document.Body);

            var table = Q(containerId);
            var colNames = "id, rid, partition_key, body, etag, ts, is_deleted, lsn";
            var colParams = "@id, @rid, @pk, @body, @etag, @ts, 0, @lsn";

            foreach (var (colName, _) in dynamicCols)
            {
                colNames += $", {Q(colName)}";
                colParams += $", @dyn_{colName}";
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO {table} ({colNames}) VALUES ({colParams})";
            cmd.Parameters.AddWithValue("@id", document.Id);
            cmd.Parameters.AddWithValue("@rid", document.Rid);
            cmd.Parameters.AddWithValue("@pk", document.PartitionKey);
            cmd.Parameters.AddWithValue("@body", document.Body.GetRawText());
            cmd.Parameters.AddWithValue("@etag", document.Etag);
            cmd.Parameters.AddWithValue("@ts", document.Ts);
            cmd.Parameters.AddWithValue("@lsn", newLsn);

            foreach (var (colName, value) in dynamicCols)
            {
                cmd.Parameters.AddWithValue($"@dyn_{colName}", value ?? (object)DBNull.Value);
            }

            cmd.ExecuteNonQuery();

            InsertChangeFeedEntry(conn, containerId, document.Id, document.PartitionKey,
                "create", document.Body.GetRawText(), null, document.Ts, document.Etag);

            return (document, newLsn);
        });
    }

    public (CosmosDocument doc, long newLsn) Replace(string databaseId, string containerId, CosmosDocument document)
        => ReplaceAsync(databaseId, containerId, document).GetAwaiter().GetResult();

    public Task<(CosmosDocument doc, long newLsn)> ReplaceAsync(string databaseId, string containerId, CosmosDocument document)
    {
        return _storage.ExecuteWriteAsync(databaseId, conn =>
        {
            var prevBody = GetCurrentBody(conn, containerId, document.Id, document.PartitionKey);

            var newLsn = IncrementLsn(conn, containerId);
            document.Lsn = newLsn;

            var dynamicCols = EnsureColumnsAndExtract(conn, databaseId, containerId, document.Body);

            var table = Q(containerId);
            var setClauses = "rid = @rid, body = @body, etag = @etag, ts = @ts, lsn = @lsn";

            foreach (var (colName, _) in dynamicCols)
            {
                setClauses += $", {Q(colName)} = @dyn_{colName}";
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE {table}
                SET {setClauses}
                WHERE id = @id AND partition_key = @pk AND is_deleted = 0
                """;
            cmd.Parameters.AddWithValue("@id", document.Id);
            cmd.Parameters.AddWithValue("@rid", document.Rid);
            cmd.Parameters.AddWithValue("@pk", document.PartitionKey);
            cmd.Parameters.AddWithValue("@body", document.Body.GetRawText());
            cmd.Parameters.AddWithValue("@etag", document.Etag);
            cmd.Parameters.AddWithValue("@ts", document.Ts);
            cmd.Parameters.AddWithValue("@lsn", newLsn);

            foreach (var (colName, value) in dynamicCols)
            {
                cmd.Parameters.AddWithValue($"@dyn_{colName}", value ?? (object)DBNull.Value);
            }

            cmd.ExecuteNonQuery();

            InsertChangeFeedEntry(conn, containerId, document.Id, document.PartitionKey,
                "replace", document.Body.GetRawText(), prevBody, document.Ts, document.Etag);

            return (document, newLsn);
        });
    }

    public long Delete(string databaseId, string containerId, string documentId, string partitionKey, bool ttlExpired = false)
        => DeleteAsync(databaseId, containerId, documentId, partitionKey, ttlExpired).GetAwaiter().GetResult();

    public Task<long> DeleteAsync(string databaseId, string containerId, string documentId, string partitionKey, bool ttlExpired = false)
    {
        return _storage.ExecuteWriteAsync(databaseId, conn =>
        {
            var prevBody = GetCurrentBody(conn, containerId, documentId, partitionKey);

            var newLsn = IncrementLsn(conn, containerId);

            var table = Q(containerId);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE id = @id AND partition_key = @pk";
            cmd.Parameters.AddWithValue("@id", documentId);
            cmd.Parameters.AddWithValue("@pk", partitionKey);
            cmd.ExecuteNonQuery();

            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            InsertChangeFeedEntry(conn, containerId, documentId, partitionKey,
                "delete", null, prevBody, ts, "", ttlExpired);

            return newLsn;
        });
    }

    /// <summary>
    /// Finds documents that have expired based on TTL rules.
    /// Returns (id, partitionKey) pairs for each expired document.
    /// </summary>
    /// <summary>
    /// Yields expired documents one at a time to avoid loading all into memory.
    /// Caller should delete each document individually (which writes to change feed atomically).
    /// </summary>
    public IEnumerable<(string id, string partitionKey)> FindExpiredDocuments(string databaseId, string containerId, int defaultTtl, long now)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);
        var table = Q(containerId);

        using var cmd = conn.CreateCommand();
        if (defaultTtl > 0)
        {
            cmd.CommandText = $"""
                SELECT id, partition_key FROM {table}
                WHERE is_deleted = 0 AND (
                    (json_extract(body, '$.ttl') > 0 AND ts + json_extract(body, '$.ttl') <= @now)
                    OR
                    (json_extract(body, '$.ttl') IS NULL AND ts + @defaultTtl <= @now)
                )
                AND COALESCE(json_extract(body, '$.ttl'), 0) != -1
                """;
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@defaultTtl", defaultTtl);
        }
        else
        {
            cmd.CommandText = $"""
                SELECT id, partition_key FROM {table}
                WHERE is_deleted = 0
                AND json_extract(body, '$.ttl') > 0
                AND ts + json_extract(body, '$.ttl') <= @now
                """;
            cmd.Parameters.AddWithValue("@now", now);
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            yield return (reader.GetString(0), reader.GetString(1));
    }

    public bool Exists(string databaseId, string containerId, string documentId, string partitionKey)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);
        var table = Q(containerId);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM {table} WHERE id = @id AND partition_key = @pk AND is_deleted = 0";
        cmd.Parameters.AddWithValue("@id", documentId);
        cmd.Parameters.AddWithValue("@pk", partitionKey);
        return cmd.ExecuteScalar() is not null;
    }

    public string? GetEtag(string databaseId, string containerId, string documentId, string partitionKey)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);
        var table = Q(containerId);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT etag FROM {table} WHERE id = @id AND partition_key = @pk AND is_deleted = 0";
        cmd.Parameters.AddWithValue("@id", documentId);
        cmd.Parameters.AddWithValue("@pk", partitionKey);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Executes a translated SQLite query and returns results as JSON elements.
    /// </summary>
    public List<JsonElement> ExecuteQuery(string databaseId, string containerId, string sqliteQuery, Dictionary<string, object>? parameters = null)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sqliteQuery;

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }
        }

        var result = new List<JsonElement>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var json = reader.GetString(0);
            result.Add(JsonParse(json));
        }
        return result;
    }

    /// <summary>
    /// Gets the set of known dynamic columns for a container.
    /// </summary>
    public HashSet<string> GetKnownColumns(string databaseId, string containerId)
    {
        var cacheKey = $"{databaseId}|{containerId}";
        if (ColumnCache.TryGetValue(cacheKey, out var cached))
            return cached;

        using var conn = _storage.GetDatabaseConnection(databaseId);
        var columns = SchemaInitializer.GetExistingColumns(conn, containerId);
        ColumnCache[cacheKey] = columns;
        return columns;
    }

    /// <summary>
    /// Invalidates the column cache for a container (call when container is deleted).
    /// </summary>
    public static void InvalidateColumnCache(string databaseId, string containerId)
    {
        ColumnCache.TryRemove($"{databaseId}|{containerId}", out _);
    }

    // --- Private helpers ---

    /// <summary>
    /// Recursively extracts JSON properties into flattened columns.
    /// Nested objects use __ as separator: address.city → address__city.
    /// Arrays are NOT extracted (they stay in body and use json_extract at query time).
    /// </summary>
    private List<(string name, object? value)> EnsureColumnsAndExtract(
        SqliteConnection conn, string databaseId, string containerId, JsonElement body)
    {
        var cacheKey = $"{databaseId}|{containerId}";
        if (!ColumnCache.TryGetValue(cacheKey, out var knownColumns))
        {
            knownColumns = SchemaInitializer.GetExistingColumns(conn, containerId);
            ColumnCache[cacheKey] = knownColumns;
        }

        var result = new List<(string name, object? value)>();

        if (body.ValueKind != JsonValueKind.Object)
            return result;

        FlattenObject(body, "", result, knownColumns, conn, containerId);
        return result;
    }

    private static void FlattenObject(JsonElement obj, string prefix,
        List<(string name, object? value)> result,
        HashSet<string> knownColumns, SqliteConnection conn, string containerId)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (SystemJsonProperties.Contains(prop.Name))
                continue;

            var colName = prefix.Length > 0 ? $"{prefix}__{prop.Name}" : prop.Name;

            // Skip properties that collide with SQLite system columns (case-insensitive).
            // e.g., user property "ETag" collides with system column "etag".
            if (prefix.Length == 0 && SystemSqliteColumns.Contains(colName))
                continue;

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    // Recurse into nested objects
                    FlattenObject(prop.Value, colName, result, knownColumns, conn, containerId);
                    break;

                case JsonValueKind.Array:
                    // Arrays stay in body — NOT extracted to columns
                    break;

                default:
                    // Scalar value — extract to column
                    var (sqliteType, value) = MapScalarValue(prop.Value);
                    if (sqliteType is not null)
                    {
                        if (!knownColumns.Contains(colName))
                        {
                            SchemaInitializer.AddColumn(conn, containerId, colName, sqliteType);
                            knownColumns.Add(colName);
                        }
                        result.Add((colName, value));
                    }
                    break;
            }
        }
    }

    private static (string? sqliteType, object? value) MapScalarValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => ("TEXT", element.GetString()),
            JsonValueKind.Number => ("REAL", element.GetDouble()),
            JsonValueKind.True => ("INTEGER", 1L),
            JsonValueKind.False => ("INTEGER", 0L),
            JsonValueKind.Null => ("TEXT", null),
            _ => (null, null)
        };
    }

    private static long IncrementLsn(SqliteConnection conn, string containerId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE _containers SET current_lsn = current_lsn + 1 WHERE id = @id;
            SELECT current_lsn FROM _containers WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", containerId);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void InsertChangeFeedEntry(SqliteConnection conn, string containerId,
        string docId, string pk, string operation, string? body, string? prevBody, long ts, string etag, bool ttlExpired = false)
    {
        var cfTable = Q(containerId + "__cf");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {cfTable} (document_id, partition_key, operation, body, previous_body, ts, etag)
            VALUES (@doc_id, @pk, @op, @body, @prev_body, @ts, @etag)
            """;
        cmd.Parameters.AddWithValue("@doc_id", docId);
        cmd.Parameters.AddWithValue("@pk", pk);
        cmd.Parameters.AddWithValue("@op", operation);
        cmd.Parameters.AddWithValue("@body", body is not null ? body : DBNull.Value);
        cmd.Parameters.AddWithValue("@prev_body", prevBody is not null ? prevBody : DBNull.Value);
        cmd.Parameters.AddWithValue("@ts", ts);
        cmd.Parameters.AddWithValue("@etag", etag);
        cmd.ExecuteNonQuery();
    }

    private static string? GetCurrentBody(SqliteConnection conn, string containerId, string docId, string pk)
    {
        var table = Q(containerId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT body FROM {table} WHERE id = @id AND partition_key = @pk AND is_deleted = 0";
        cmd.Parameters.AddWithValue("@id", docId);
        cmd.Parameters.AddWithValue("@pk", pk);
        return cmd.ExecuteScalar() as string;
    }

    private static CosmosDocument ReadDocument(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Rid = reader.GetString(1),
        PartitionKey = reader.GetString(2),
        Body = JsonParse(reader.GetString(3)),
        Etag = reader.GetString(4),
        Ts = reader.GetInt64(5),
        IsDeleted = reader.GetInt32(6) != 0,
        Lsn = reader.GetInt64(7)
    };

    private static JsonElement JsonParse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string Q(string name) => SchemaInitializer.QuoteName(name);

    /// <summary>
    /// Binds the three literals compared by <see cref="PartitionKeyPredicate.BuildSql"/>.
    /// </summary>
    private static void BindPartitionKey(SqliteCommand cmd, string paramPrefix, string partitionKey)
    {
        var bounds = PartitionKeyPredicate.Compute(partitionKey);
        cmd.Parameters.AddWithValue($"{paramPrefix}_exact", bounds.Exact);
        cmd.Parameters.AddWithValue($"{paramPrefix}_lo", bounds.RangeLow);
        cmd.Parameters.AddWithValue($"{paramPrefix}_hi", bounds.RangeHigh);
    }
}

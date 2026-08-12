using System.Text.Json;
using Cosmos.Emulator.Core.Helpers;
using Cosmos.Emulator.Core.Models;
using Microsoft.Data.Sqlite;

namespace Cosmos.Emulator.Storage.Repositories;

public class ChangeFeedRepository
{
    private readonly SqliteStorageProvider _storage;

    public ChangeFeedRepository(SqliteStorageProvider storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// LatestVersion: reads current state of documents changed after the given LSN.
    /// Runs on the writer channel to avoid WAL contention with concurrent writes.
    /// </summary>
    public List<CosmosDocument> ReadLatestVersion(
        string databaseId, string containerId,
        string? partitionKey, long afterLsn, int maxItems, EpkRange? epkRange = null)
        => ReadLatestVersionAsync(databaseId, containerId, partitionKey, afterLsn, maxItems, epkRange).GetAwaiter().GetResult();

    public Task<List<CosmosDocument>> ReadLatestVersionAsync(
        string databaseId, string containerId,
        string? partitionKey, long afterLsn, int maxItems, EpkRange? epkRange = null)
    {
        return _storage.ExecuteWriteAsync(databaseId, conn =>
        {
            var table = QuoteName(containerId);

            using var cmd = conn.CreateCommand();
            var where = "lsn > @lsn AND is_deleted = 0";
            if (partitionKey is not null)
            {
                // A hierarchical container may be read by a prefix of its key components.
                where += " AND " + PartitionKeyPredicate.BuildSql("partition_key", "@pk");
                BindPartitionKey(cmd, "@pk", partitionKey);
            }
            if (epkRange is { } epk)
            {
                where += " AND " + EpkFilter.BuildSql("partition_key", "@epk");
                EpkFilter.Bind(cmd, "@epk", epk);
            }

            cmd.CommandText = $"""
                SELECT id, rid, partition_key, body, etag, ts, is_deleted, lsn
                FROM {table}
                WHERE {where}
                ORDER BY lsn
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@lsn", afterLsn);
            cmd.Parameters.AddWithValue("@limit", maxItems);

            var result = new List<CosmosDocument>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CosmosDocument
                {
                    Id = reader.GetString(0),
                    Rid = reader.GetString(1),
                    PartitionKey = reader.GetString(2),
                    Body = JsonParse(reader.GetString(3)),
                    Etag = reader.GetString(4),
                    Ts = reader.GetInt64(5),
                    IsDeleted = reader.GetInt32(6) != 0,
                    Lsn = reader.GetInt64(7)
                });
            }
            return result;
        });
    }

    /// <summary>
    /// AllVersionsAndDeletes: reads change feed log entries after the given LSN.
    /// Runs on the writer channel to avoid WAL contention with concurrent writes.
    /// </summary>
    public List<ChangeFeedEntry> ReadAllVersionsAndDeletes(
        string databaseId, string containerId,
        string? partitionKey, long afterLsn, int maxItems, EpkRange? epkRange = null)
        => ReadAllVersionsAndDeletesAsync(databaseId, containerId, partitionKey, afterLsn, maxItems, epkRange).GetAwaiter().GetResult();

    public Task<List<ChangeFeedEntry>> ReadAllVersionsAndDeletesAsync(
        string databaseId, string containerId,
        string? partitionKey, long afterLsn, int maxItems, EpkRange? epkRange = null)
    {
        return _storage.ExecuteWriteAsync(databaseId, conn =>
        {
            var cfTable = QuoteName(containerId + "__cf");

            using var cmd = conn.CreateCommand();
            var where = "lsn > @lsn";
            if (partitionKey is not null)
            {
                // A hierarchical container may be read by a prefix of its key components.
                where += " AND " + PartitionKeyPredicate.BuildSql("partition_key", "@pk");
                BindPartitionKey(cmd, "@pk", partitionKey);
            }
            if (epkRange is { } epk)
            {
                where += " AND " + EpkFilter.BuildSql("partition_key", "@epk");
                EpkFilter.Bind(cmd, "@epk", epk);
            }

            cmd.CommandText = $"""
                SELECT lsn, document_id, partition_key, operation, body, previous_body, ts, etag
                FROM {cfTable}
                WHERE {where}
                ORDER BY lsn
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@lsn", afterLsn);
            cmd.Parameters.AddWithValue("@limit", maxItems);

            var result = new List<ChangeFeedEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ChangeFeedEntry
                {
                    Lsn = reader.GetInt64(0),
                    DocumentId = reader.GetString(1),
                    PartitionKey = reader.GetString(2),
                    Operation = reader.GetString(3),
                    Body = reader.IsDBNull(4) ? null : JsonParse(reader.GetString(4)),
                    PreviousBody = reader.IsDBNull(5) ? null : JsonParse(reader.GetString(5)),
                    Ts = reader.GetInt64(6),
                    Etag = reader.GetString(7)
                });
            }
            return result;
        });
    }

    public long GetCurrentLsn(string databaseId, string containerId)
        => GetCurrentLsnAsync(databaseId, containerId).GetAwaiter().GetResult();

    public Task<long> GetCurrentLsnAsync(string databaseId, string containerId)
    {
        return _storage.ExecuteWriteAsync(databaseId, conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT current_lsn FROM _containers WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", containerId);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        });
    }

    private static JsonElement JsonParse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string QuoteName(string name) => $"[{name.Replace("]", "]]")}]";

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

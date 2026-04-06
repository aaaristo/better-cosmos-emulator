using System.Text.Json;
using Cosmos.Emulator.Core.Models;
using Microsoft.Data.Sqlite;

namespace Cosmos.Emulator.Storage.Repositories;

public class ContainerRepository
{
    private readonly SqliteStorageProvider _storage;

    public ContainerRepository(SqliteStorageProvider storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Resolves either a user ID or _rid to the actual container user ID.
    /// </summary>
    public string? ResolveId(string databaseId, string idOrRid)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM _containers WHERE id = @id OR rid = @id";
        cmd.Parameters.AddWithValue("@id", idOrRid);
        return cmd.ExecuteScalar() as string;
    }

    public CosmosContainer? Get(string databaseId, string containerId)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, rid, self_link, etag, ts, partition_key_json,
                   indexing_policy, default_ttl, pkrange_rid, current_lsn
            FROM _containers WHERE id = @id OR rid = @id
            """;
        cmd.Parameters.AddWithValue("@id", containerId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return ReadContainer(reader);
    }

    public List<CosmosContainer> List(string databaseId)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, rid, self_link, etag, ts, partition_key_json,
                   indexing_policy, default_ttl, pkrange_rid, current_lsn
            FROM _containers ORDER BY id
            """;

        var result = new List<CosmosContainer>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadContainer(reader));
        }
        return result;
    }

    public void Create(string databaseId, CosmosContainer container, string pkrangeRid)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO _containers (id, rid, self_link, etag, ts, partition_key_json,
                                     indexing_policy, default_ttl, pkrange_rid, current_lsn)
            VALUES (@id, @rid, @self_link, @etag, @ts, @pk_json,
                    @idx_policy, @default_ttl, @pkrange_rid, 0)
            """;
        cmd.Parameters.AddWithValue("@id", container.Id);
        cmd.Parameters.AddWithValue("@rid", container.Rid);
        cmd.Parameters.AddWithValue("@self_link", container.Self);
        cmd.Parameters.AddWithValue("@etag", container.Etag);
        cmd.Parameters.AddWithValue("@ts", container.Ts);
        cmd.Parameters.AddWithValue("@pk_json", JsonSerializer.Serialize(container.PartitionKey));
        cmd.Parameters.AddWithValue("@idx_policy", JsonSerializer.Serialize(container.IndexingPolicy));
        cmd.Parameters.AddWithValue("@default_ttl", container.DefaultTtl.HasValue ? container.DefaultTtl.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@pkrange_rid", pkrangeRid);
        cmd.ExecuteNonQuery();

        Schema.SchemaInitializer.CreateContainerTables(conn, container.Id);
    }

    public void Replace(string databaseId, CosmosContainer container)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE _containers SET
                etag = @etag,
                ts = @ts,
                partition_key_json = @pk_json,
                indexing_policy = @idx_policy,
                default_ttl = @default_ttl
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", container.Id);
        cmd.Parameters.AddWithValue("@etag", container.Etag);
        cmd.Parameters.AddWithValue("@ts", container.Ts);
        cmd.Parameters.AddWithValue("@pk_json", JsonSerializer.Serialize(container.PartitionKey));
        cmd.Parameters.AddWithValue("@idx_policy", JsonSerializer.Serialize(container.IndexingPolicy));
        cmd.Parameters.AddWithValue("@default_ttl", container.DefaultTtl.HasValue ? container.DefaultTtl.Value : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string databaseId, string containerId)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);

        Schema.SchemaInitializer.DropContainerTables(conn, containerId);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM _containers WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", containerId);
        cmd.ExecuteNonQuery();

        DocumentRepository.InvalidateColumnCache(databaseId, containerId);
    }

    public bool Exists(string databaseId, string containerId)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM _containers WHERE id = @id OR rid = @id";
        cmd.Parameters.AddWithValue("@id", containerId);
        return cmd.ExecuteScalar() is not null;
    }

    public string? GetPkRangeRid(string databaseId, string containerId)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pkrange_rid FROM _containers WHERE id = @id OR rid = @id";
        cmd.Parameters.AddWithValue("@id", containerId);
        return cmd.ExecuteScalar() as string;
    }

    public string? GetContainerRid(string databaseId, string containerId)
    {
        using var conn = _storage.GetDatabaseConnection(databaseId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rid FROM _containers WHERE id = @id OR rid = @id";
        cmd.Parameters.AddWithValue("@id", containerId);
        return cmd.ExecuteScalar() as string;
    }

    private static CosmosContainer ReadContainer(SqliteDataReader reader)
    {
        var pkJson = reader.GetString(5);
        var idxJson = reader.GetString(6);

        return new CosmosContainer
        {
            Id = reader.GetString(0),
            Rid = reader.GetString(1),
            Self = reader.GetString(2),
            Etag = reader.GetString(3),
            Ts = reader.GetInt64(4),
            PartitionKey = JsonSerializer.Deserialize<PartitionKeyDefinition>(pkJson)!,
            IndexingPolicy = JsonSerializer.Deserialize<IndexingPolicy>(idxJson)!,
            DefaultTtl = reader.IsDBNull(7) ? null : reader.GetInt32(7)
        };
    }
}

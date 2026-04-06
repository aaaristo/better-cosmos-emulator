using Cosmos.Emulator.Core.Models;
using Microsoft.Data.Sqlite;

namespace Cosmos.Emulator.Storage.Repositories;

public class DatabaseRepository
{
    private readonly SqliteStorageProvider _storage;

    public DatabaseRepository(SqliteStorageProvider storage)
    {
        _storage = storage;
    }

    public CosmosDatabase? Get(string id)
    {
        using var conn = _storage.GetCatalogConnection();
        using var cmd = conn.CreateCommand();
        // Support lookup by either user ID or _rid (with - → / conversion for URL-encoded rids)
        var ridVariant = id.Replace("-", "/");
        cmd.CommandText = "SELECT id, rid, self_link, etag, ts FROM databases WHERE id = @id OR rid = @id OR rid = @rv";
        cmd.Parameters.AddWithValue("@rv", ridVariant);
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return ReadDatabase(reader);
    }

    public List<CosmosDatabase> List()
    {
        using var conn = _storage.GetCatalogConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, rid, self_link, etag, ts FROM databases ORDER BY id";

        var result = new List<CosmosDatabase>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadDatabase(reader));
        }
        return result;
    }

    public void Create(CosmosDatabase database)
    {
        using var conn = _storage.GetCatalogConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO databases (id, rid, self_link, etag, ts)
            VALUES (@id, @rid, @self_link, @etag, @ts)
            """;
        cmd.Parameters.AddWithValue("@id", database.Id);
        cmd.Parameters.AddWithValue("@rid", database.Rid);
        cmd.Parameters.AddWithValue("@self_link", database.Self);
        cmd.Parameters.AddWithValue("@etag", database.Etag);
        cmd.Parameters.AddWithValue("@ts", database.Ts);
        cmd.ExecuteNonQuery();

        // Create the SQLite file for this database and initialize its schema
        using var dbConn = _storage.GetDatabaseConnection(database.Id);
        Schema.SchemaInitializer.InitializeDatabase(dbConn);
    }

    /// <summary>
    /// Resolves either a user ID or _rid to the actual database user ID (for file path mapping).
    /// </summary>
    public string? ResolveId(string idOrRid)
    {
        using var conn = _storage.GetCatalogConnection();
        using var cmd = conn.CreateCommand();
        var ridVariant = idOrRid.Replace("-", "/");
        cmd.CommandText = "SELECT id FROM databases WHERE id = @id OR rid = @id OR rid = @rv";
        cmd.Parameters.AddWithValue("@id", idOrRid);
        cmd.Parameters.AddWithValue("@rv", ridVariant);
        return cmd.ExecuteScalar() as string;
    }

    public void Delete(string id)
    {
        using var conn = _storage.GetCatalogConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM databases WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();

        _storage.DeleteDatabaseFile(id);
    }

    public bool Exists(string id)
    {
        using var conn = _storage.GetCatalogConnection();
        using var cmd = conn.CreateCommand();
        var ridVariant = id.Replace("-", "/");
        cmd.CommandText = "SELECT 1 FROM databases WHERE id = @id OR rid = @id OR rid = @rv";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@rv", ridVariant);
        return cmd.ExecuteScalar() is not null;
    }

    private static CosmosDatabase ReadDatabase(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Rid = reader.GetString(1),
        Self = reader.GetString(2),
        Etag = reader.GetString(3),
        Ts = reader.GetInt64(4)
    };
}

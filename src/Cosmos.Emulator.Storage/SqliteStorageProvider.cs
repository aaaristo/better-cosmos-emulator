using Microsoft.Data.Sqlite;

namespace Cosmos.Emulator.Storage;

public class SqliteStorageProvider
{
    private readonly string _dataDirectory;
    private readonly string _catalogConnectionString;

    public SqliteStorageProvider(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        _catalogConnectionString = $"Data Source={Path.Combine(_dataDirectory, "_catalog.db")}";
    }

    public void Initialize()
    {
        Directory.CreateDirectory(_dataDirectory);
        using var conn = GetCatalogConnection();
        Schema.SchemaInitializer.InitializeCatalog(conn);
    }

    public SqliteConnection GetCatalogConnection()
    {
        var conn = new SqliteConnection(_catalogConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// Resolves a database ID or _rid to the actual user-facing ID by checking the catalog.
    /// Returns the input unchanged if no catalog match (assumes it's already the user ID).
    /// </summary>
    public string ResolveDatabaseId(string idOrRid)
    {
        using var conn = GetCatalogConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM databases WHERE rid = @rid";
        cmd.Parameters.AddWithValue("@rid", idOrRid);
        var result = cmd.ExecuteScalar() as string;
        return result ?? idOrRid;
    }

    public SqliteConnection GetDatabaseConnection(string databaseId)
    {
        // Always try to resolve — the caller might pass a _rid or a user-facing name
        databaseId = ResolveDatabaseId(databaseId);

        var dbPath = Path.Combine(_dataDirectory, $"{SanitizeFileName(databaseId)}.db");
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public void DeleteDatabaseFile(string databaseId)
    {
        var dbPath = Path.Combine(_dataDirectory, $"{SanitizeFileName(databaseId)}.db");

        // Clear SQLite connection pool so file handles are released
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={dbPath}"));

        var basePath = Path.Combine(_dataDirectory, SanitizeFileName(databaseId));
        foreach (var ext in new[] { ".db", ".db-wal", ".db-shm" })
        {
            var path = basePath + ext;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    public bool DatabaseFileExists(string databaseId)
    {
        var dbPath = Path.Combine(_dataDirectory, $"{SanitizeFileName(databaseId)}.db");
        return File.Exists(dbPath);
    }

    private static string SanitizeFileName(string name)
    {
        // Replace any chars that aren't safe for filenames
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            sanitized[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        }
        return new string(sanitized);
    }
}

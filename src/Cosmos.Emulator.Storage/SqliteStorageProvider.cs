using Microsoft.Data.Sqlite;

namespace Cosmos.Emulator.Storage;

public class SqliteStorageProvider
{
    private readonly string _dataDirectory;
    private readonly string _catalogConnectionString;
    private readonly bool _inMemory;

    // For in-memory mode, keep one connection per database alive so the shared cache isn't dropped
    private readonly Dictionary<string, SqliteConnection> _keepAlive = new();

    public SqliteStorageProvider(string dataDirectory, bool inMemory = false)
    {
        _dataDirectory = dataDirectory;
        _inMemory = inMemory;
        _catalogConnectionString = inMemory
            ? "Data Source=file:_catalog?mode=memory&cache=shared"
            : $"Data Source={Path.Combine(_dataDirectory, "_catalog.db")}";
    }

    public void Initialize()
    {
        if (!_inMemory)
            Directory.CreateDirectory(_dataDirectory);

        if (_inMemory)
        {
            // Keep a connection alive so the shared in-memory DB persists
            var keep = new SqliteConnection(_catalogConnectionString);
            keep.Open();
            _keepAlive["_catalog"] = keep;
        }

        using var conn = GetCatalogConnection();
        Schema.SchemaInitializer.InitializeCatalog(conn);
    }

    public SqliteConnection GetCatalogConnection()
    {
        var conn = new SqliteConnection(_catalogConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = _inMemory
            ? "PRAGMA foreign_keys=ON;"
            : "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
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
        // SDK replaces / with - in _rid URL paths
        var ridVariant = idOrRid.Replace("-", "/");
        cmd.CommandText = "SELECT id FROM databases WHERE rid = @rid OR rid = @rv";
        cmd.Parameters.AddWithValue("@rid", idOrRid);
        cmd.Parameters.AddWithValue("@rv", ridVariant);
        var result = cmd.ExecuteScalar() as string;
        return result ?? idOrRid;
    }

    public SqliteConnection GetDatabaseConnection(string databaseId)
    {
        // Always try to resolve — the caller might pass a _rid or a user-facing name
        databaseId = ResolveDatabaseId(databaseId);

        string connStr;
        if (_inMemory)
        {
            var safeName = SanitizeFileName(databaseId);
            connStr = $"Data Source=file:{safeName}?mode=memory&cache=shared";

            // Ensure keep-alive connection exists for this database
            if (!_keepAlive.ContainsKey(safeName))
            {
                var keep = new SqliteConnection(connStr);
                keep.Open();
                _keepAlive[safeName] = keep;
            }
        }
        else
        {
            var dbPath = Path.Combine(_dataDirectory, $"{SanitizeFileName(databaseId)}.db");
            connStr = $"Data Source={dbPath}";
        }

        var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = _inMemory
            ? "PRAGMA foreign_keys=ON;"
            : "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public void DeleteDatabaseFile(string databaseId)
    {
        var safeName = SanitizeFileName(databaseId);

        if (_inMemory)
        {
            // Close the keep-alive connection — this drops the in-memory DB
            if (_keepAlive.Remove(safeName, out var keep))
                keep.Dispose();
            return;
        }

        var dbPath = Path.Combine(_dataDirectory, $"{safeName}.db");

        // Clear SQLite connection pool so file handles are released
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={dbPath}"));

        var basePath = Path.Combine(_dataDirectory, safeName);
        foreach (var ext in new[] { ".db", ".db-wal", ".db-shm" })
        {
            var path = basePath + ext;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    public bool DatabaseFileExists(string databaseId)
    {
        if (_inMemory)
        {
            var safeName = SanitizeFileName(databaseId);
            return _keepAlive.ContainsKey(safeName);
        }

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

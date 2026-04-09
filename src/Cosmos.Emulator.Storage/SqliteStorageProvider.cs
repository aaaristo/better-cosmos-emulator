using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Cosmos.Emulator.Storage;

/// <summary>
/// Dedicated writer channel for a single SQLite database. All writes go through a single
/// persistent connection, eliminating WAL lock acquisition overhead. Reads use separate
/// connections (WAL allows concurrent readers).
/// </summary>
internal class DatabaseWriter : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly Channel<WriteCommand> _channel;
    private readonly Task _processLoop;

    public DatabaseWriter(string connectionString, bool inMemory)
    {
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        // WAL + synchronous=NORMAL for performance.
        cmd.CommandText = inMemory
            ? "PRAGMA foreign_keys=ON;"
            : "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();

        _channel = Channel.CreateUnbounded<WriteCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _processLoop = Task.Run(ProcessLoop);
    }

    public SqliteConnection Connection => _conn;

    public Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> action)
    {
        var cmd = new WriteCommand<T>(action);
        _channel.Writer.TryWrite(cmd);
        return cmd.Task;
    }

    private async Task ProcessLoop()
    {
        await foreach (var cmd in _channel.Reader.ReadAllAsync())
        {
            cmd.Execute(_conn);
        }
    }

    public void Dispose()
    {
        _channel.Writer.Complete();
        _processLoop.Wait(TimeSpan.FromSeconds(5));
        _conn.Dispose();
    }
}

internal abstract class WriteCommand
{
    public abstract void Execute(SqliteConnection conn);
}

internal class WriteCommand<T> : WriteCommand
{
    private readonly Func<SqliteConnection, T> _action;
    private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WriteCommand(Func<SqliteConnection, T> action) => _action = action;
    public Task<T> Task => _tcs.Task;

    public override void Execute(SqliteConnection conn)
    {
        try
        {
            _tcs.SetResult(_action(conn));
        }
        catch (Exception ex)
        {
            _tcs.SetException(ex);
        }
    }
}

public class SqliteStorageProvider
{
    private readonly string _dataDirectory;
    private readonly string _catalogConnectionString;
    private readonly bool _inMemory;

    private readonly ConcurrentDictionary<string, DatabaseWriter> _writers = new();

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
            ? "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"
            : "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
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
            ? "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"
            : "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// Executes a write operation on the dedicated writer connection for the given database.
    /// All writes to the same database are serialized through a single connection,
    /// eliminating WAL lock contention.
    /// </summary>
    public Task<T> ExecuteWriteAsync<T>(string databaseId, Func<SqliteConnection, T> action)
    {
        databaseId = ResolveDatabaseId(databaseId);
        var writer = GetOrCreateWriter(databaseId);
        return writer.ExecuteAsync(action);
    }

    private DatabaseWriter GetOrCreateWriter(string databaseId)
    {
        return _writers.GetOrAdd(databaseId, id =>
        {
            var connStr = GetConnectionString(id);
            return new DatabaseWriter(connStr, _inMemory);
        });
    }

    private string GetConnectionString(string databaseId)
    {
        if (_inMemory)
        {
            var safeName = SanitizeFileName(databaseId);
            return $"Data Source=file:{safeName}?mode=memory&cache=shared";
        }
        var dbPath = Path.Combine(_dataDirectory, $"{SanitizeFileName(databaseId)}.db");
        return $"Data Source={dbPath}";
    }

    public void DeleteDatabaseFile(string databaseId)
    {
        var safeName = SanitizeFileName(databaseId);

        // Dispose the dedicated writer for this database
        if (_writers.TryRemove(databaseId, out var writer))
            writer.Dispose();

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

using Microsoft.Data.Sqlite;

namespace Cosmos.Emulator.Storage.Schema;

public static class SchemaInitializer
{
    public static void InitializeCatalog(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS databases (
                id        TEXT    PRIMARY KEY,
                rid       TEXT    NOT NULL UNIQUE,
                self_link TEXT    NOT NULL,
                etag      TEXT    NOT NULL,
                ts        INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public static void InitializeDatabase(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS _containers (
                id                 TEXT    PRIMARY KEY,
                rid                TEXT    NOT NULL UNIQUE,
                self_link          TEXT    NOT NULL,
                etag               TEXT    NOT NULL,
                ts                 INTEGER NOT NULL,
                partition_key_json TEXT    NOT NULL,
                indexing_policy    TEXT    NOT NULL,
                default_ttl        INTEGER,
                pkrange_rid        TEXT    NOT NULL,
                current_lsn        INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public static void CreateContainerTables(SqliteConnection connection, string containerName)
    {
        var quoted = QuoteName(containerName);
        var cfQuoted = QuoteName(containerName + "__cf");
        var pkIdx = QuoteName(containerName + "__pk");
        var lsnIdx = QuoteName(containerName + "__lsn");

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE {quoted} (
                id            TEXT    NOT NULL,
                rid           TEXT    NOT NULL UNIQUE,
                partition_key TEXT    NOT NULL,
                body          TEXT    NOT NULL,
                etag          TEXT    NOT NULL,
                ts            INTEGER NOT NULL,
                is_deleted    INTEGER NOT NULL DEFAULT 0,
                lsn           INTEGER NOT NULL,
                PRIMARY KEY (id, partition_key)
            );

            CREATE INDEX {pkIdx}  ON {quoted}(partition_key);
            CREATE INDEX {lsnIdx} ON {quoted}(lsn);

            CREATE TABLE {cfQuoted} (
                lsn           INTEGER PRIMARY KEY AUTOINCREMENT,
                document_id   TEXT    NOT NULL,
                partition_key TEXT    NOT NULL,
                operation     TEXT    NOT NULL,
                body          TEXT,
                previous_body TEXT,
                ts            INTEGER NOT NULL,
                etag          TEXT    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public static void DropContainerTables(SqliteConnection connection, string containerName)
    {
        var quoted = QuoteName(containerName);
        var cfQuoted = QuoteName(containerName + "__cf");

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            DROP TABLE IF EXISTS {cfQuoted};
            DROP TABLE IF EXISTS {quoted};
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets existing user-defined columns (excludes system columns) for a container table.
    /// </summary>
    public static HashSet<string> GetExistingColumns(SqliteConnection connection, string containerName)
    {
        var systemColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "rid", "partition_key", "body", "etag", "ts", "is_deleted", "lsn"
        };

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({QuoteName(containerName)})";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1); // column name is at index 1
            if (!systemColumns.Contains(name))
                columns.Add(name);
        }
        return columns;
    }

    /// <summary>
    /// Adds a new column to the container table.
    /// </summary>
    public static void AddColumn(SqliteConnection connection, string containerName, string columnName, string sqliteType)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {QuoteName(containerName)} ADD COLUMN {QuoteName(columnName)} {sqliteType}";
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists (case-insensitive match) — safe to ignore
        }
    }

    /// <summary>
    /// Creates SQLite composite indexes based on the container's indexing policy.
    /// Drops existing composite indexes first, then recreates from the policy.
    /// Composite index on [/Repo, /Path, /Deleted] becomes a SQLite index on [Repo, Path, Deleted].
    /// Nested paths like /address/city map to column [address__city].
    /// </summary>
    public static void SyncCompositeIndexes(
        SqliteConnection connection, string containerName,
        List<List<Core.Models.CompositeIndex>> compositeIndexes,
        HashSet<string> existingColumns)
    {
        // Drop all existing composite indexes for this container
        using (var listCmd = connection.CreateCommand())
        {
            listCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name=@tbl AND name LIKE '%__cix_%'";
            listCmd.Parameters.AddWithValue("@tbl", containerName);
            using var reader = listCmd.ExecuteReader();
            var toDrop = new List<string>();
            while (reader.Read())
                toDrop.Add(reader.GetString(0));
            foreach (var idx in toDrop)
            {
                using var dropCmd = connection.CreateCommand();
                dropCmd.CommandText = $"DROP INDEX IF EXISTS {QuoteName(idx)}";
                dropCmd.ExecuteNonQuery();
            }
        }

        // Create new composite indexes
        for (int i = 0; i < compositeIndexes.Count; i++)
        {
            var composite = compositeIndexes[i];
            var columnNames = new List<string>();
            var allColumnsExist = true;

            foreach (var path in composite)
            {
                // /Repo -> Repo, /address/city -> address__city
                var colName = path.Path.TrimStart('/').Replace("/", "__");
                if (!existingColumns.Contains(colName))
                {
                    allColumnsExist = false;
                    break;
                }
                var order = path.Order.Equals("descending", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
                columnNames.Add($"{QuoteName(colName)} {order}");
            }

            if (!allColumnsExist || columnNames.Count == 0)
                continue; // Skip — columns don't exist yet, index will be created when data arrives

            var idxName = QuoteName($"{containerName}__cix_{i}");
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"CREATE INDEX IF NOT EXISTS {idxName} ON {QuoteName(containerName)}({string.Join(", ", columnNames)})";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Creates a single-column SQLite index.
    /// </summary>
    public static void CreateColumnIndex(SqliteConnection connection, string containerName, string columnName)
    {
        var idxName = QuoteName($"{containerName}__ix_{columnName}");
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE INDEX IF NOT EXISTS {idxName} ON {QuoteName(containerName)}({QuoteName(columnName)})";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Pre-creates columns and single-column indexes for all paths referenced in the
    /// indexing policy (composite indexes). This ensures columns and indexes exist
    /// before any documents are inserted.
    /// </summary>
    public static void EnsureColumnsFromPolicy(
        SqliteConnection connection, string containerName,
        Core.Models.IndexingPolicy policy)
    {
        var existingColumns = GetExistingColumns(connection, containerName);

        // Collect all paths that need columns from composite indexes
        // /Repo -> Repo, /address/city -> address__city
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var composite in policy.CompositeIndexes)
        {
            foreach (var ci in composite)
            {
                var col = ci.Path.TrimStart('/').Replace("/", "__");
                if (!string.IsNullOrEmpty(col) && col != "*")
                    paths.Add(col);
            }
        }

        // Create any missing columns and single-column indexes
        foreach (var col in paths)
        {
            if (!existingColumns.Contains(col))
            {
                AddColumn(connection, containerName, col, "TEXT");
                existingColumns.Add(col);
            }
            CreateColumnIndex(connection, containerName, col);
        }
    }

    public static string QuoteName(string name) => $"[{name.Replace("]", "]]")}]";
}

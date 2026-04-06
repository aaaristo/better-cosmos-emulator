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
        cmd.ExecuteNonQuery();
    }

    public static string QuoteName(string name) => $"[{name.Replace("]", "]]")}]";
}

using Microsoft.Data.Sqlite;
using Xunit;

namespace Cosmos.Emulator.Tests.Unit;

public class SqliteJsonTest
{
    [Fact]
    public void JsonObject_WithJson_ShouldEmbedSubObject()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT sqlite_version()";
        var version = cmd.ExecuteScalar()!.ToString();

        cmd.CommandText = "SELECT json_object('payload', json('{\"name\":\"Alice\"}'))";
        var withJson = cmd.ExecuteScalar()!.ToString();

        cmd.CommandText = "SELECT json_object('payload', '{\"name\":\"Alice\"}')";
        var withoutJson = cmd.ExecuteScalar()!.ToString();

        // Write results for diagnosis
        Assert.True(true, $"SQLite {version}: with json()={withJson}, without={withoutJson}");

        // Verify json() makes it embed as sub-object
        Assert.Equal("{\"payload\":{\"name\":\"Alice\"}}", withJson);

        // Now test with an actual table column (not a literal)
        cmd.CommandText = "CREATE TABLE t (body TEXT)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO t VALUES ('{\"name\":\"Alice\",\"age\":30}')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT json_object('payload', json(body)) FROM t";
        var fromColumn = cmd.ExecuteScalar()!.ToString();

        // This is the critical test: does json(body) from a column embed correctly?
        Assert.Equal("{\"payload\":{\"name\":\"Alice\",\"age\":30}}", fromColumn);

        // Now test: does JsonDocument.Parse + System.Text.Json serialize correctly?
        var element = System.Text.Json.JsonDocument.Parse(fromColumn).RootElement.Clone();
        var serialized = System.Text.Json.JsonSerializer.Serialize(
            new { Documents = new[] { element } });

        // Should be: {"Documents":[{"payload":{"name":"Alice","age":30}}]}
        // NOT: {"Documents":["{\\"payload\\":...}"]}
        Assert.Contains("\"payload\":{\"name\"", serialized);

        // Test with etag-style escaped quotes in the body (like our documents have)
        cmd.CommandText = "INSERT INTO t VALUES ('{\"_etag\":\"\\u002200000000\\u0022\",\"name\":\"Bob\"}')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT json_object('payload', json(body)) FROM t WHERE json_extract(body, '$.name') = 'Bob'";
        var withEtag = cmd.ExecuteScalar()!.ToString();

        // Should still embed properly, not stringify
        Assert.Contains("\"payload\":{\"_etag\"", withEtag);

        // Test json_each + json_extract for object arrays (JOIN support)
        cmd.CommandText = """
            CREATE TABLE docs (body TEXT);
            INSERT INTO docs VALUES ('{"name":"Alice","sizes":[{"key":"s","desc":"Small"},{"key":"l","desc":"Large"}]}');
        """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            SELECT json_extract(body, '$.name'), json_extract(j.value, '$.key'), json_extract(j.value, '$.desc')
            FROM docs CROSS JOIN json_each(json_extract(body, '$.sizes')) AS j
        """;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add($"{reader.GetString(0)},{reader.GetString(1)},{reader.GetString(2)}");

        Assert.Equal(2, rows.Count);
        Assert.Equal("Alice,s,Small", rows[0]);
        Assert.Equal("Alice,l,Large", rows[1]);
    }

    [Fact]
    public void JsonEach_WithParameterArray_ShouldMatchColumnValues()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE test (
                id TEXT PRIMARY KEY,
                partition_key TEXT,
                body TEXT,
                etag TEXT,
                ts INTEGER,
                is_deleted INTEGER DEFAULT 0,
                lsn INTEGER,
                rid TEXT,
                [Path] TEXT
            );
            INSERT INTO test VALUES ('1', '["pk1"]', '{"id":"1","Path":"/repo/item-a"}', '"e1"', 1, 0, 1, 'r1', '/repo/item-a');
            INSERT INTO test VALUES ('2', '["pk2"]', '{"id":"2","Path":"/repo/item-b"}', '"e2"', 1, 0, 2, 'r2', '/repo/item-b');
            INSERT INTO test VALUES ('3', '["pk1"]', '{"id":"3","Path":"/repo/item-c"}', '"e3"', 1, 0, 3, 'r3', '/repo/item-c');
        """;
        cmd.ExecuteNonQuery();

        // Verify json_each works in a simple SELECT
        cmd.CommandText = "SELECT count(*) FROM json_each('[\"a\",\"b\"]')";
        var jeCount = (long)cmd.ExecuteScalar()!;
        Assert.Equal(2, jeCount); // basic json_each works

        // Test: IN with subquery using json_each (inline literal) — works in SQLite
        cmd.CommandText = """
            SELECT count(*) FROM test WHERE is_deleted = 0
            AND [Path] IN (SELECT json_each.value FROM json_each('["/repo/item-a","/repo/item-b"]'))
        """;
        var count1 = (long)cmd.ExecuteScalar()!;
        Assert.Equal(2, count1);

        // Our emulator's fix: expand ARRAY_CONTAINS(@param, expr) into expr IN (@p0, @p1, ...)
        cmd.CommandText = """
            SELECT count(*) FROM test WHERE is_deleted = 0
            AND [Path] IN (@p0, @p1)
        """;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@p0", "/repo/item-a");
        cmd.Parameters.AddWithValue("@p1", "/repo/item-b");
        var count2 = (long)cmd.ExecuteScalar()!;
        Assert.Equal(2, count2);
    }
}

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
    }
}

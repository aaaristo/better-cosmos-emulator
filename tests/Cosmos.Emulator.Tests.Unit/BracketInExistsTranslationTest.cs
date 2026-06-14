using System.Collections.Generic;
using System.Linq;
using Cosmos.Emulator.QueryEngine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Cosmos.Emulator.Tests.Unit;

/// <summary>
/// Covers the cloud "SearchCreateCustomer" query family (EF Core nested bracket notation +
/// parameterized-collection .Contains()). Each query is translated and then executed against an
/// in-memory SQLite table that mirrors the emulator's hybrid-column layout, verifying real results.
/// </summary>
public class BracketInExistsTranslationTest
{
    // Mirrors a container table: full document in `body`, scalars flattened into columns.
    private static SqliteConnection NewSeededDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE [c] (
                [Id] TEXT,
                [Chain] TEXT,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                body TEXT
            );
            INSERT INTO [c] ([Id],[Chain],is_deleted,body) VALUES
              ('c1','chainA',0,'{"Id":"c1","Chain":"chainA","Ocid":{"Value":"g1"}}'),
              ('c2','chainA',0,'{"Id":"c2","Chain":"chainA","Ocid":{"Value":"g2"}}'),
              ('c3','chainA',0,'{"Id":"c3","Chain":"chainA","Ocid":{"Value":"g3"}}');
            """;
        cmd.ExecuteNonQuery();
        return conn;
    }

    private static HashSet<string> KnownColumns() => new() { "Id", "Chain" }; // Ocid is a nested object, not a column

    private static List<string> RunIds(SqliteConnection conn, TranslatedQuery q)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = q.Sql;
        foreach (var (k, v) in q.Parameters)
            cmd.Parameters.AddWithValue(k, v);

        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // Each row is a single json_object(...) column; pull the Id out of it.
            var json = reader.GetString(0);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            ids.Add(doc.RootElement.GetProperty("Id").GetString()!);
        }
        return ids;
    }

    [Fact]
    public void NestedBracket_NoLongerMistranslatesToArrayIndex()
    {
        var engine = new CosmosSqlQueryEngine();
        var q = engine.Translate(
            "SELECT c[\"Id\"], c[\"Ocid\"][\"Value\"] AS Ocid, c[\"Chain\"] FROM root c",
            "c", KnownColumns());

        // c["Ocid"]["Value"] must become a nested object path, not array-index access.
        Assert.Contains("json_extract(body, '$.Ocid.Value')", q.Sql);
        Assert.DoesNotContain("'$[' || 'Value' || ']'", q.Sql);
        // Bracket select items must get real aliases, not the duplicate "$1" placeholder.
        Assert.Contains("'Id'", q.Sql);
        Assert.Contains("'Chain'", q.Sql);
        Assert.DoesNotContain("'$1'", q.Sql);
    }

    [Fact]
    public void InClause_WithArrayValuedParameter_ExpandsAndMatches()
    {
        using var conn = NewSeededDb();
        var engine = new CosmosSqlQueryEngine();
        var q = engine.Translate(
            "SELECT c[\"Id\"], c[\"Ocid\"][\"Value\"] AS Ocid, c[\"Chain\"] FROM root c WHERE c[\"Ocid\"][\"Value\"] IN (@__ocids_0)",
            "c", KnownColumns(),
            new Dictionary<string, object> { ["@__ocids_0"] = "[\"g1\",\"g3\"]" });

        Assert.Equal(new[] { "c1", "c3" }, RunIds(conn, q).OrderBy(x => x));
    }

    [Fact]
    public void ExistsSubquery_OverParameterArray_Matches()
    {
        using var conn = NewSeededDb();
        var engine = new CosmosSqlQueryEngine();
        var q = engine.Translate(
            "SELECT c[\"Id\"] FROM root c WHERE EXISTS (SELECT VALUE 1 FROM o IN @__ocids_0 WHERE o = c[\"Ocid\"][\"Value\"])",
            "c", KnownColumns(),
            new Dictionary<string, object> { ["@__ocids_0"] = "[\"g2\",\"g3\"]" });

        Assert.Equal(new[] { "c2", "c3" }, RunIds(conn, q).OrderBy(x => x));
    }

    [Fact]
    public void ExistsSubquery_OverEmbeddedArray_Matches()
    {
        using var conn = NewSeededDb();
        using (var seed = conn.CreateCommand())
        {
            // Give c1 an embedded array of tags; others have none.
            seed.CommandText =
                "UPDATE [c] SET body = '{\"Id\":\"c1\",\"Chain\":\"chainA\",\"Tags\":[{\"Value\":\"t1\"},{\"Value\":\"t2\"}]}' WHERE [Id]='c1'";
            seed.ExecuteNonQuery();
        }

        var engine = new CosmosSqlQueryEngine();
        var q = engine.Translate(
            "SELECT c[\"Id\"] FROM root c WHERE EXISTS (SELECT VALUE 1 FROM t IN c[\"Tags\"] WHERE t[\"Value\"] IN (@__tags_0))",
            "c", KnownColumns(),
            new Dictionary<string, object> { ["@__tags_0"] = "[\"t2\"]" });

        Assert.Equal(new[] { "c1" }, RunIds(conn, q));
    }

    [Fact]
    public void InClause_WithEmptyArrayParameter_MatchesNothing()
    {
        using var conn = NewSeededDb();
        var engine = new CosmosSqlQueryEngine();
        var q = engine.Translate(
            "SELECT c[\"Id\"] FROM root c WHERE c[\"Ocid\"][\"Value\"] IN (@__ocids_0)",
            "c", KnownColumns(),
            new Dictionary<string, object> { ["@__ocids_0"] = "[]" });

        Assert.Empty(RunIds(conn, q));
    }
}

using System.Collections.ObjectModel;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class SqliteIndexTests
{
    private readonly CosmosClient _client;
    private readonly Storage.SqliteStorageProvider _storage;

    public SqliteIndexTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
        _storage = fixture.Storage;
    }

    [Fact]
    public async Task CreateContainer_WithCompositeIndex_CreatesSqliteIndex()
    {
        var db = await CreateTempDatabase();
        var containerName = $"ci-{Guid.NewGuid():N}"[..20];

        var props = new ContainerProperties(containerName, "/pk")
        {
            IndexingPolicy = new IndexingPolicy
            {
                CompositeIndexes =
                {
                    new Collection<CompositePath>
                    {
                        new CompositePath { Path = "/Repo", Order = CompositePathSortOrder.Ascending },
                        new CompositePath { Path = "/Path", Order = CompositePathSortOrder.Ascending },
                        new CompositePath { Path = "/Deleted", Order = CompositePathSortOrder.Ascending }
                    }
                }
            }
        };

        await db.CreateContainerAsync(props);

        // Verify SQLite columns were pre-created
        var dbName = (await db.ReadAsync()).Resource.Id;
        using var conn = _storage.GetDatabaseConnection(dbName);
        var columns = Storage.Schema.SchemaInitializer.GetExistingColumns(conn, containerName);

        columns.ShouldContain("Repo");
        columns.ShouldContain("Path");
        columns.ShouldContain("Deleted");

        // Verify SQLite indexes exist
        var indexes = GetIndexes(conn, containerName);
        indexes.ShouldContain(idx => idx.Contains("cix_0"), "Composite index should exist");
        indexes.ShouldContain(idx => idx.Contains("ix_Repo"), "Single-column index on Repo should exist");
        indexes.ShouldContain(idx => idx.Contains("ix_Path"), "Single-column index on Path should exist");
        indexes.ShouldContain(idx => idx.Contains("ix_Deleted"), "Single-column index on Deleted should exist");
    }

    [Fact]
    public async Task ReplaceContainer_AddCompositeIndex_CreatesSqliteIndex()
    {
        var db = await CreateTempDatabase();
        var containerName = $"ri-{Guid.NewGuid():N}"[..20];

        // Create container without composite indexes
        var container = (await db.CreateContainerAsync(containerName, "/pk")).Container;

        // Insert a document first so columns exist
        await container.CreateItemAsync(
            new { id = "1", pk = "a", Status = "active", Priority = 5 },
            new PartitionKey("a"));

        // Now add composite index via replace
        var resp = await container.ReadContainerAsync();
        resp.Resource.IndexingPolicy.CompositeIndexes.Add(
            new Collection<CompositePath>
            {
                new CompositePath { Path = "/Status", Order = CompositePathSortOrder.Ascending },
                new CompositePath { Path = "/Priority", Order = CompositePathSortOrder.Descending }
            });
        await container.ReplaceContainerAsync(resp.Resource);

        // Verify composite index was created in SQLite
        var dbName = (await db.ReadAsync()).Resource.Id;
        using var conn = _storage.GetDatabaseConnection(dbName);
        var indexes = GetIndexes(conn, containerName);

        indexes.ShouldContain(idx => idx.Contains("cix_0"), "Composite index should exist after replace");
    }

    [Fact]
    public async Task CreateContainer_WithCompositeIndex_ColumnsExistBeforeInsert()
    {
        var db = await CreateTempDatabase();
        var containerName = $"pre-{Guid.NewGuid():N}"[..20];

        var props = new ContainerProperties(containerName, "/pk")
        {
            IndexingPolicy = new IndexingPolicy
            {
                CompositeIndexes =
                {
                    new Collection<CompositePath>
                    {
                        new CompositePath { Path = "/name", Order = CompositePathSortOrder.Ascending },
                        new CompositePath { Path = "/age", Order = CompositePathSortOrder.Descending }
                    }
                }
            }
        };

        await db.CreateContainerAsync(props);

        // Columns should exist even before any documents are inserted
        var dbName = (await db.ReadAsync()).Resource.Id;
        using var conn = _storage.GetDatabaseConnection(dbName);
        var columns = Storage.Schema.SchemaInitializer.GetExistingColumns(conn, containerName);

        columns.ShouldContain("name");
        columns.ShouldContain("age");
    }

    [Fact]
    public async Task CreateContainer_WithNestedCompositeIndex_CreatesCorrectColumn()
    {
        var db = await CreateTempDatabase();
        var containerName = $"nc-{Guid.NewGuid():N}"[..20];

        var props = new ContainerProperties(containerName, "/pk")
        {
            IndexingPolicy = new IndexingPolicy
            {
                CompositeIndexes =
                {
                    new Collection<CompositePath>
                    {
                        new CompositePath { Path = "/address/city", Order = CompositePathSortOrder.Ascending },
                        new CompositePath { Path = "/address/zip", Order = CompositePathSortOrder.Ascending }
                    }
                }
            }
        };

        await db.CreateContainerAsync(props);

        var dbName = (await db.ReadAsync()).Resource.Id;
        using var conn = _storage.GetDatabaseConnection(dbName);
        var columns = Storage.Schema.SchemaInitializer.GetExistingColumns(conn, containerName);

        // Nested paths should be flattened with __
        columns.ShouldContain("address__city");
        columns.ShouldContain("address__zip");
    }

    private static List<string> GetIndexes(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name=@tbl";
        cmd.Parameters.AddWithValue("@tbl", tableName);

        var indexes = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            indexes.Add(reader.GetString(0));
        return indexes;
    }

    private async Task<Database> CreateTempDatabase()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        return (await _client.CreateDatabaseAsync(dbName)).Database;
    }
}

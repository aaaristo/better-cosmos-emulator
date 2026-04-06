using System.Collections.ObjectModel;
using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class IndexingPolicyTests
{
    private readonly CosmosClient _client;

    public IndexingPolicyTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task ReplaceContainer_AddCompositeIndex_ShouldPersist()
    {
        var db = await CreateTempDatabase();
        var container = (await db.CreateContainerAsync("idx-test", "/pk")).Container;

        // Read current container
        var containerResponse = await container.ReadContainerAsync();
        var indexingPolicy = containerResponse.Resource.IndexingPolicy;

        // Verify no composite indexes initially
        indexingPolicy.CompositeIndexes.Count.ShouldBe(0);

        // Add composite index [Repo, Path, Deleted] — same as user's code
        var desired = new[] { "/Repo", "/Path", "/Deleted" };
        var exists = indexingPolicy.CompositeIndexes.Any(ci =>
            ci.Count == desired.Length &&
            ci.Select(p => p.Path).SequenceEqual(desired));

        exists.ShouldBeFalse();

        var compositeIndex = new Collection<CompositePath>
        {
            new CompositePath { Path = "/Repo", Order = CompositePathSortOrder.Ascending },
            new CompositePath { Path = "/Path", Order = CompositePathSortOrder.Ascending },
            new CompositePath { Path = "/Deleted", Order = CompositePathSortOrder.Ascending }
        };
        indexingPolicy.CompositeIndexes.Add(compositeIndex);

        // Replace container with updated policy
        var replaceResponse = await container.ReplaceContainerAsync(containerResponse.Resource);
        replaceResponse.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        // Read back and verify composite index was persisted
        var readBack = await container.ReadContainerAsync();
        readBack.Resource.IndexingPolicy.CompositeIndexes.Count.ShouldBe(1);

        var savedIndex = readBack.Resource.IndexingPolicy.CompositeIndexes[0];
        savedIndex.Count.ShouldBe(3);
        savedIndex[0].Path.ShouldBe("/Repo");
        savedIndex[1].Path.ShouldBe("/Path");
        savedIndex[2].Path.ShouldBe("/Deleted");
        savedIndex[0].Order.ShouldBe(CompositePathSortOrder.Ascending);
    }

    [Fact]
    public async Task CreateContainer_WithCompositeIndex_ShouldPreserve()
    {
        var db = await CreateTempDatabase();

        var props = new ContainerProperties("ci-test", "/pk")
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

        var response = await db.CreateContainerAsync(props);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);

        var readBack = await db.GetContainer("ci-test").ReadContainerAsync();
        readBack.Resource.IndexingPolicy.CompositeIndexes.Count.ShouldBe(1);
        readBack.Resource.IndexingPolicy.CompositeIndexes[0][0].Path.ShouldBe("/name");
        readBack.Resource.IndexingPolicy.CompositeIndexes[0][1].Path.ShouldBe("/age");
    }

    [Fact]
    public async Task CreateContainer_WithExcludedPaths_ShouldPreserve()
    {
        var db = await CreateTempDatabase();

        var props = new ContainerProperties("excl-test", "/pk")
        {
            IndexingPolicy = new IndexingPolicy
            {
                IncludedPaths = { new IncludedPath { Path = "/*" } },
                ExcludedPaths =
                {
                    new ExcludedPath { Path = "/secret/*" },
                    new ExcludedPath { Path = "/\"_etag\"/?" }
                }
            }
        };

        var response = await db.CreateContainerAsync(props);
        var readBack = await db.GetContainer("excl-test").ReadContainerAsync();

        readBack.Resource.IndexingPolicy.ExcludedPaths
            .ShouldContain(p => p.Path == "/secret/*");
    }

    private async Task<Database> CreateTempDatabase()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        return (await _client.CreateDatabaseAsync(dbName)).Database;
    }
}

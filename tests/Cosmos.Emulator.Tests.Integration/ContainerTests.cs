using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class ContainerTests
{
    private readonly CosmosClient _client;

    public ContainerTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task CreateContainer_ShouldSucceed()
    {
        var db = await CreateTempDatabase();

        var response = await db.CreateContainerAsync("test-container", "/partitionKey");

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        response.Resource.Id.ShouldBe("test-container");
        response.Resource.PartitionKeyPath.ShouldBe("/partitionKey");
    }

    [Fact]
    public async Task CreateContainerIfNotExists_ShouldBeIdempotent()
    {
        var db = await CreateTempDatabase();

        var r1 = await db.CreateContainerIfNotExistsAsync("test-container", "/pk");
        r1.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);

        var r2 = await db.CreateContainerIfNotExistsAsync("test-container", "/pk");
        r2.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateContainer_WithIndexingPolicy_ShouldPreservePolicy()
    {
        var db = await CreateTempDatabase();

        var containerProperties = new ContainerProperties("indexed-container", "/pk")
        {
            IndexingPolicy = new IndexingPolicy
            {
                IndexingMode = IndexingMode.Consistent,
                Automatic = true,
                IncludedPaths = { new IncludedPath { Path = "/*" } },
                ExcludedPaths = { new ExcludedPath { Path = "/secret/*" } }
            }
        };

        var response = await db.CreateContainerAsync(containerProperties);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);

        var read = await db.GetContainer("indexed-container").ReadContainerAsync();
        read.Resource.IndexingPolicy.IndexingMode.ShouldBe(IndexingMode.Consistent);
    }

    [Fact]
    public async Task DeleteContainer_ShouldSucceed()
    {
        var db = await CreateTempDatabase();
        await db.CreateContainerAsync("to-delete", "/pk");

        var response = await db.GetContainer("to-delete").DeleteContainerAsync();
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ListContainers_ShouldIncludeCreated()
    {
        var db = await CreateTempDatabase();
        await db.CreateContainerAsync("container-a", "/pk");
        await db.CreateContainerAsync("container-b", "/pk");

        var iterator = db.GetContainerQueryIterator<ContainerProperties>();
        var containers = new List<ContainerProperties>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            containers.AddRange(page);
        }

        containers.Count.ShouldBeGreaterThanOrEqualTo(2);
        containers.ShouldContain(c => c.Id == "container-a");
        containers.ShouldContain(c => c.Id == "container-b");
    }

    private async Task<Database> CreateTempDatabase()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var response = await _client.CreateDatabaseAsync(dbName);
        return response.Database;
    }
}

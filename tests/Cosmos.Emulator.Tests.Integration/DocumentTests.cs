using System.Net;
using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class DocumentTests
{
    private readonly CosmosClient _client;

    public DocumentTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task CreateItem_ShouldSucceed()
    {
        var container = await CreateTempContainer();

        var item = new { id = "item1", partitionKey = "pk1", name = "Alice", age = 30 };
        var response = await container.CreateItemAsync(item, new PartitionKey("pk1"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact(Timeout = 15000)]
    public async Task ReadItem_ShouldReturnCreatedItem()
    {
        var container = await CreateTempContainer();

        var item = new { id = "item1", partitionKey = "pk1", name = "Bob", age = 25 };
        await container.CreateItemAsync(item, new PartitionKey("pk1"));

        // Use stream API to bypass SDK's internal partition key range routing
        using var streamResponse = await container.ReadItemStreamAsync("item1", new PartitionKey("pk1"));
        streamResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = await System.Text.Json.JsonDocument.ParseAsync(streamResponse.Content);
        doc.RootElement.GetProperty("name").GetString().ShouldBe("Bob");
    }

    [Fact]
    public async Task ReplaceItem_ShouldUpdateDocument()
    {
        var container = await CreateTempContainer();

        var item = new { id = "item1", partitionKey = "pk1", name = "Charlie", age = 40 };
        await container.CreateItemAsync(item, new PartitionKey("pk1"));

        var updated = new { id = "item1", partitionKey = "pk1", name = "Charlie Updated", age = 41 };
        var response = await container.ReplaceItemAsync(updated, "item1", new PartitionKey("pk1"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var read = await container.ReadItemAsync<dynamic>("item1", new PartitionKey("pk1"));
        string name = read.Resource.name;
        name.ShouldBe("Charlie Updated");
    }

    [Fact]
    public async Task UpsertItem_ShouldCreateOrReplace()
    {
        var container = await CreateTempContainer();

        var item = new { id = "item1", partitionKey = "pk1", name = "Dave", age = 35 };

        // First upsert = create
        var r1 = await container.UpsertItemAsync(item, new PartitionKey("pk1"));
        r1.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Second upsert = replace
        var updated = new { id = "item1", partitionKey = "pk1", name = "Dave Updated", age = 36 };
        var r2 = await container.UpsertItemAsync(updated, new PartitionKey("pk1"));
        r2.StatusCode.ShouldBe(HttpStatusCode.OK);

        var read = await container.ReadItemAsync<dynamic>("item1", new PartitionKey("pk1"));
        string name = read.Resource.name;
        name.ShouldBe("Dave Updated");
    }

    [Fact]
    public async Task DeleteItem_ShouldSucceed()
    {
        var container = await CreateTempContainer();

        var item = new { id = "item1", partitionKey = "pk1", name = "Eve" };
        await container.CreateItemAsync(item, new PartitionKey("pk1"));

        var response = await container.DeleteItemAsync<dynamic>("item1", new PartitionKey("pk1"));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Reading should now fail
        var ex = await Should.ThrowAsync<CosmosException>(
            () => container.ReadItemAsync<dynamic>("item1", new PartitionKey("pk1")));
        ex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateDuplicateItem_ShouldReturn409()
    {
        var container = await CreateTempContainer();

        var item = new { id = "dup1", partitionKey = "pk1", name = "Frank" };
        await container.CreateItemAsync(item, new PartitionKey("pk1"));

        var ex = await Should.ThrowAsync<CosmosException>(
            () => container.CreateItemAsync(item, new PartitionKey("pk1")));
        ex.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SameIdDifferentPartition_ShouldBothExist()
    {
        var container = await CreateTempContainer();

        var item1 = new { id = "shared-id", partitionKey = "pk1", name = "In PK1" };
        var item2 = new { id = "shared-id", partitionKey = "pk2", name = "In PK2" };

        await container.CreateItemAsync(item1, new PartitionKey("pk1"));
        await container.CreateItemAsync(item2, new PartitionKey("pk2"));

        var r1 = await container.ReadItemAsync<dynamic>("shared-id", new PartitionKey("pk1"));
        var r2 = await container.ReadItemAsync<dynamic>("shared-id", new PartitionKey("pk2"));

        ((string)r1.Resource.name).ShouldBe("In PK1");
        ((string)r2.Resource.name).ShouldBe("In PK2");
    }

    [Fact]
    public async Task EtagConcurrency_ShouldEnforceIfMatch()
    {
        var container = await CreateTempContainer();

        var item = new { id = "etag-test", partitionKey = "pk1", name = "Original" };
        var createResponse = await container.CreateItemAsync(item, new PartitionKey("pk1"));
        var etag = createResponse.ETag;

        // Replace with correct etag
        var updated = new { id = "etag-test", partitionKey = "pk1", name = "Updated" };
        var replaceResponse = await container.ReplaceItemAsync(
            updated, "etag-test", new PartitionKey("pk1"),
            new ItemRequestOptions { IfMatchEtag = etag });
        replaceResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Replace with stale etag should fail
        var stale = new { id = "etag-test", partitionKey = "pk1", name = "Stale" };
        var ex = await Should.ThrowAsync<CosmosException>(
            () => container.ReplaceItemAsync(
                stale, "etag-test", new PartitionKey("pk1"),
                new ItemRequestOptions { IfMatchEtag = etag })); // old etag
        ex.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    private async Task<Container> CreateTempContainer()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        return (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/partitionKey")).Container;
    }
}

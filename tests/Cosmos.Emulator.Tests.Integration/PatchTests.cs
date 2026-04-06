using System.Net;
using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class PatchTests
{
    private readonly CosmosClient _client;

    public PatchTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task PatchItem_Set_ShouldUpdateField()
    {
        var container = await CreateTempContainer();
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Alice", age = 30 },
            new PartitionKey("pk1"));

        var response = await container.PatchItemAsync<dynamic>("1", new PartitionKey("pk1"),
            new[] { PatchOperation.Set("/name", "Alice Updated") });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ((string)response.Resource.name).ShouldBe("Alice Updated");
    }

    [Fact]
    public async Task PatchItem_Add_ShouldAddNewField()
    {
        var container = await CreateTempContainer();
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Alice" },
            new PartitionKey("pk1"));

        var response = await container.PatchItemAsync<dynamic>("1", new PartitionKey("pk1"),
            new[] { PatchOperation.Add("/city", "Seattle") });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ((string)response.Resource.city).ShouldBe("Seattle");
    }

    [Fact]
    public async Task PatchItem_Remove_ShouldRemoveField()
    {
        var container = await CreateTempContainer();
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Alice", age = 30 },
            new PartitionKey("pk1"));

        var response = await container.PatchItemAsync<dynamic>("1", new PartitionKey("pk1"),
            new[] { PatchOperation.Remove("/age") });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Verify field was removed by reading back
        var readBack = await container.ReadItemAsync<dynamic>("1", new PartitionKey("pk1"));
        bool hasAge = false;
        try { var _ = (int)readBack.Resource.age; hasAge = true; } catch { }
        hasAge.ShouldBeFalse();
    }

    [Fact]
    public async Task PatchItem_Replace_ShouldReplaceExistingField()
    {
        var container = await CreateTempContainer();
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Alice", age = 30 },
            new PartitionKey("pk1"));

        var response = await container.PatchItemAsync<dynamic>("1", new PartitionKey("pk1"),
            new[] { PatchOperation.Replace("/age", 31) });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ((int)response.Resource.age).ShouldBe(31);
    }

    [Fact]
    public async Task PatchItem_Increment_ShouldIncrementNumber()
    {
        var container = await CreateTempContainer();
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Alice", score = 100 },
            new PartitionKey("pk1"));

        var response = await container.PatchItemAsync<dynamic>("1", new PartitionKey("pk1"),
            new[] { PatchOperation.Increment("/score", 25) });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ((int)response.Resource.score).ShouldBe(125);
    }

    [Fact]
    public async Task PatchItem_MultipleOperations_ShouldApplyAll()
    {
        var container = await CreateTempContainer();
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Alice", age = 30, city = "Seattle" },
            new PartitionKey("pk1"));

        var response = await container.PatchItemAsync<dynamic>("1", new PartitionKey("pk1"),
            new[]
            {
                PatchOperation.Set("/name", "Alice Updated"),
                PatchOperation.Increment("/age", 1),
                PatchOperation.Remove("/city"),
                PatchOperation.Add("/country", "US")
            });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ((string)response.Resource.name).ShouldBe("Alice Updated");
        ((int)response.Resource.age).ShouldBe(31);
        ((string)response.Resource.country).ShouldBe("US");
    }

    [Fact]
    public async Task PatchItem_NestedPath_ShouldWork()
    {
        var container = await CreateTempContainer();
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", address = new { city = "Seattle", zip = "98101" } },
            new PartitionKey("pk1"));

        var response = await container.PatchItemAsync<dynamic>("1", new PartitionKey("pk1"),
            new[] { PatchOperation.Set("/address/city", "Portland") });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ((string)response.Resource.address.city).ShouldBe("Portland");
    }

    [Fact]
    public async Task PatchItem_EtagMismatch_ShouldReturn412()
    {
        var container = await CreateTempContainer();
        var createResponse = await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Alice" },
            new PartitionKey("pk1"));

        // First patch succeeds
        await container.PatchItemAsync<dynamic>("1", new PartitionKey("pk1"),
            new[] { PatchOperation.Set("/name", "Bob") });

        // Second patch with stale etag should fail
        var ex = await Should.ThrowAsync<CosmosException>(() =>
            container.PatchItemAsync<dynamic>("1", new PartitionKey("pk1"),
                new[] { PatchOperation.Set("/name", "Charlie") },
                new PatchItemRequestOptions { IfMatchEtag = createResponse.ETag }));

        ex.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    private async Task<Container> CreateTempContainer()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        return (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/partitionKey")).Container;
    }
}

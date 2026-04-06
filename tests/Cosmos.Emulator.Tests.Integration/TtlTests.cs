using System.Net;
using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class TtlTests
{
    private readonly CosmosClient _client;

    public TtlTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task ContainerWithTtl_DocumentExpires()
    {
        var db = await CreateTempDatabase();

        // Create container with 2-second TTL
        var props = new ContainerProperties($"ttl-{Guid.NewGuid():N}"[..20], "/pk")
        {
            DefaultTimeToLive = 2
        };
        var container = (await db.CreateContainerAsync(props)).Container;

        // Insert document
        await container.CreateItemAsync(
            new { id = "1", pk = "a", name = "ephemeral" },
            new PartitionKey("a"));

        // Should exist immediately
        var response = await container.ReadItemAsync<dynamic>("1", new PartitionKey("a"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Wait for TTL + cleanup interval
        await Task.Delay(TimeSpan.FromSeconds(8));

        // Should be gone
        var ex = await Should.ThrowAsync<CosmosException>(
            () => container.ReadItemAsync<dynamic>("1", new PartitionKey("a")));
        ex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ContainerWithTtl_DocumentOverrideNeverExpires()
    {
        var db = await CreateTempDatabase();

        var props = new ContainerProperties($"ttl-{Guid.NewGuid():N}"[..20], "/pk")
        {
            DefaultTimeToLive = 2
        };
        var container = (await db.CreateContainerAsync(props)).Container;

        // Insert document with ttl = -1 (never expires)
        await container.CreateItemAsync(
            new { id = "1", pk = "a", name = "permanent", ttl = -1 },
            new PartitionKey("a"));

        await Task.Delay(TimeSpan.FromSeconds(8));

        // Should still exist
        var response = await container.ReadItemAsync<dynamic>("1", new PartitionKey("a"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ContainerWithoutTtl_DocumentDoesNotExpire()
    {
        var db = await CreateTempDatabase();

        // No DefaultTimeToLive set
        var container = (await db.CreateContainerAsync($"nottl-{Guid.NewGuid():N}"[..20], "/pk")).Container;

        await container.CreateItemAsync(
            new { id = "1", pk = "a", name = "persistent" },
            new PartitionKey("a"));

        await Task.Delay(TimeSpan.FromSeconds(3));

        // Should still exist
        var response = await container.ReadItemAsync<dynamic>("1", new PartitionKey("a"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ContainerWithTtl_ExpirationAppearsInChangeFeed()
    {
        var db = await CreateTempDatabase();

        var props = new ContainerProperties($"ttl-{Guid.NewGuid():N}"[..20], "/pk")
        {
            DefaultTimeToLive = 2
        };
        var container = (await db.CreateContainerAsync(props)).Container;

        // Insert document that will expire
        await container.CreateItemAsync(
            new { id = "1", pk = "a", name = "willExpire" },
            new PartitionKey("a"));

        // Read change feed from beginning — should see the create
        var feedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.AllVersionsAndDeletes);

        var createChanges = new List<dynamic>();
        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync();
            if (response.StatusCode == HttpStatusCode.NotModified)
                break;
            createChanges.AddRange(response);
        }
        createChanges.Count.ShouldBeGreaterThanOrEqualTo(1);

        // Wait for TTL expiration
        await Task.Delay(TimeSpan.FromSeconds(8));

        // Read change feed again — should see the delete from TTL
        var allChanges = new List<dynamic>();
        var iterator2 = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.AllVersionsAndDeletes);

        while (iterator2.HasMoreResults)
        {
            var response = await iterator2.ReadNextAsync();
            if (response.StatusCode == HttpStatusCode.NotModified)
                break;
            allChanges.AddRange(response);
        }

        // Should have at least create + delete (from TTL expiration)
        allChanges.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    private async Task<Database> CreateTempDatabase()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        return (await _client.CreateDatabaseAsync(dbName)).Database;
    }
}

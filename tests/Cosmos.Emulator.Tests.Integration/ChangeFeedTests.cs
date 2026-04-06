using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class ChangeFeedTests
{
    private readonly CosmosClient _client;

    public ChangeFeedTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task LatestVersionChangeFeed_ShouldReturnChanges()
    {
        var container = await CreateTempContainer();

        // Insert some items
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Item1" },
            new PartitionKey("pk1"));
        await container.CreateItemAsync(
            new { id = "2", partitionKey = "pk1", name = "Item2" },
            new PartitionKey("pk1"));

        // Read change feed from beginning
        var feedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.LatestVersion);

        var allChanges = new List<dynamic>();
        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                break;
            allChanges.AddRange(response);
        }

        allChanges.Count.ShouldBe(2);
    }

    [Fact]
    public async Task LatestVersionChangeFeed_ShouldReturnNotModifiedWhenCaughtUp()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Item1" },
            new PartitionKey("pk1"));

        var feedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.LatestVersion);

        // Read first batch
        var firstBatch = await feedIterator.ReadNextAsync();
        firstBatch.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        firstBatch.Count.ShouldBe(1);

        // Reading again should return 304
        var secondBatch = await feedIterator.ReadNextAsync();
        secondBatch.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task LatestVersionChangeFeed_ShouldPickUpNewChanges()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Initial" },
            new PartitionKey("pk1"));

        var feedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.LatestVersion);

        // Drain initial changes
        string? continuationToken = null;
        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                continuationToken = response.ContinuationToken;
                break;
            }
        }

        // Add more items
        await container.CreateItemAsync(
            new { id = "2", partitionKey = "pk1", name = "New" },
            new PartitionKey("pk1"));

        // Resume from continuation
        var resumedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.ContinuationToken(continuationToken!),
            ChangeFeedMode.LatestVersion);

        var newChanges = new List<dynamic>();
        while (resumedIterator.HasMoreResults)
        {
            var response = await resumedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                break;
            newChanges.AddRange(response);
        }

        newChanges.Count.ShouldBe(1);
        ((string)newChanges[0].name).ShouldBe("New");
    }

    [Fact(Skip = "AllVersionsAndDeletes requires additional SDK protocol support")]
    public async Task AllVersionsAndDeletesChangeFeed_ShouldCaptureDeletes()
    {
        var container = await CreateTempContainer();

        // Create, then delete an item
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "ToDelete" },
            new PartitionKey("pk1"));
        await container.DeleteItemAsync<dynamic>("1", new PartitionKey("pk1"));

        // Read change feed in AllVersionsAndDeletes mode
        var feedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.AllVersionsAndDeletes);

        var allChanges = new List<dynamic>();
        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                break;
            allChanges.AddRange(response);
        }

        // Should have at least create + delete
        allChanges.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    private async Task<Container> CreateTempContainer()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        return (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/partitionKey")).Container;
    }
}

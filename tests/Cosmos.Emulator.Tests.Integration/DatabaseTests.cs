using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class DatabaseTests
{
    private readonly CosmosClient _client;

    public DatabaseTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task CreateDatabase_ShouldSucceed()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var response = await _client.CreateDatabaseAsync(dbName);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        response.Resource.Id.ShouldBe(dbName);
    }

    [Fact]
    public async Task CreateDatabaseIfNotExists_ShouldBeIdempotent()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";

        var r1 = await _client.CreateDatabaseIfNotExistsAsync(dbName);
        r1.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);

        var r2 = await _client.CreateDatabaseIfNotExistsAsync(dbName);
        r2.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        r2.Resource.Id.ShouldBe(dbName);
    }

    [Fact]
    public async Task GetDatabase_ShouldReturnExisting()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        await _client.CreateDatabaseAsync(dbName);

        var response = await _client.GetDatabase(dbName).ReadAsync();
        response.Resource.Id.ShouldBe(dbName);
    }

    [Fact]
    public async Task DeleteDatabase_ShouldSucceed()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        await _client.CreateDatabaseAsync(dbName);

        var response = await _client.GetDatabase(dbName).DeleteAsync();
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ListDatabases_ShouldIncludeCreated()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        await _client.CreateDatabaseAsync(dbName);

        var iterator = _client.GetDatabaseQueryIterator<DatabaseProperties>();
        var databases = new List<DatabaseProperties>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            databases.AddRange(page);
        }

        databases.ShouldContain(d => d.Id == dbName);
    }
}

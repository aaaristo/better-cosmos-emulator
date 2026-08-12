using System.Text;
using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

/// <summary>
/// Validation of the partition key definition at container creation. Driven over raw
/// HTTP because the SDK's <see cref="ContainerProperties"/> will not construct these
/// shapes — but a REST client, another language SDK, or hand-written tooling can.
/// </summary>
[Collection("Emulator")]
public class PartitionKeyValidationTests
{
    private readonly CosmosClient _client;

    public PartitionKeyValidationTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task CreateContainer_WithThreePaths_ShouldSucceed()
    {
        // Three is the documented maximum, so it must sit inside the limit.
        var response = await CreateContainerOverRawHttp(
            "{\"paths\":[\"/a\",\"/b\",\"/c\"],\"kind\":\"MultiHash\",\"version\":2}");

        response.Status.ShouldBe(System.Net.HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateContainer_WithFourPaths_ShouldReject()
    {
        var response = await CreateContainerOverRawHttp(
            "{\"paths\":[\"/a\",\"/b\",\"/c\",\"/d\"],\"kind\":\"MultiHash\",\"version\":2}");

        response.Status.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        response.Body.ShouldContain("Too many partition key paths (4)");
        response.Body.ShouldContain("maximum of 3");
    }

    [Fact]
    public async Task CreateContainer_WithNoPaths_ShouldReject()
    {
        // Would otherwise be accepted and funnel every document into the key '[]'.
        var response = await CreateContainerOverRawHttp("{\"paths\":[],\"kind\":\"Hash\",\"version\":2}");

        response.Status.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        response.Body.ShouldContain("at least one path");
    }

    [Fact]
    public async Task CreateContainer_WithPartitionKeyMissingPaths_ShouldRejectRatherThanFail()
    {
        // 'paths' is a required member, so deserialization throws — that must surface as a
        // 400 and not as an unhandled 500.
        var response = await CreateContainerOverRawHttp("{\"kind\":\"Hash\",\"version\":2}");

        response.Status.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        response.Body.ShouldContain("malformed");
    }

    [Fact]
    public async Task CreateContainer_RejectedByValidation_ShouldNotCreateTheContainer()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        await _client.CreateDatabaseAsync(dbName);
        var collName = $"test-coll-{Guid.NewGuid():N}";

        var response = await CreateContainerOverRawHttp(
            "{\"paths\":[\"/a\",\"/b\",\"/c\",\"/d\"],\"kind\":\"MultiHash\",\"version\":2}",
            dbName, collName);
        response.Status.ShouldBe(System.Net.HttpStatusCode.BadRequest);

        var ex = await Should.ThrowAsync<CosmosException>(
            () => _client.GetDatabase(dbName).GetContainer(collName).ReadContainerAsync());
        ex.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    private async Task<(System.Net.HttpStatusCode Status, string Body)> CreateContainerOverRawHttp(
        string partitionKeyJson, string? dbName = null, string? collName = null)
    {
        dbName ??= $"test-db-{Guid.NewGuid():N}";
        collName ??= $"test-coll-{Guid.NewGuid():N}";

        if (!await DatabaseExists(dbName))
            await _client.CreateDatabaseAsync(dbName);

        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        })
        { BaseAddress = _client.Endpoint };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/dbs/{dbName}/colls")
        {
            Content = new StringContent(
                $"{{\"id\":\"{collName}\",\"partitionKey\":{partitionKeyJson}}}",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "type=master&ver=1.0&sig=test");
        request.Headers.TryAddWithoutValidation("x-ms-date", DateTime.UtcNow.ToString("R"));

        var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<bool> DatabaseExists(string dbName)
    {
        try
        {
            await _client.GetDatabase(dbName).ReadAsync();
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}

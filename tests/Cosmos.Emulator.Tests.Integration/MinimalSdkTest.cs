using System.Diagnostics;
using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class MinimalSdkTest
{
    private readonly CosmosClient _client;

    public MinimalSdkTest(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task CreateDatabase_AndReadBack()
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] Starting CreateDatabase...");

        var dbName = $"test-{Guid.NewGuid():N}"[..20];
        var response = await _client.CreateDatabaseAsync(dbName);
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] CreateDatabase returned {response.StatusCode}");

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);

        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] Reading database back...");
        var readResponse = await _client.GetDatabase(dbName).ReadAsync();
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] ReadDatabase returned {readResponse.StatusCode}");

        readResponse.Resource.Id.ShouldBe(dbName);
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] Test complete");
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class QueryPlanTests
{
    private readonly EmulatorFixture _fixture;

    public QueryPlanTests(EmulatorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task QueryPlanRequest_ShouldReturnPlan()
    {
        // Setup: create a database and container
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _fixture.Client.CreateDatabaseAsync(dbName)).Database;
        var container = (await db.CreateContainerAsync($"qp-{Guid.NewGuid():N}"[..20], "/pk")).Container;

        // Send a raw query plan request like older SDKs do
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var http = new HttpClient(handler);

        var requestBody = JsonSerializer.Serialize(new
        {
            query = "SELECT DISTINCT VALUE c[\"Repo\"] FROM root c",
            parameters = Array.Empty<object>()
        });

        var baseUrl = _fixture.Client.Endpoint.ToString().TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl}/dbs/{dbName}/colls/{container.Id}/docs")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/query+json")
        };

        // Add required headers
        request.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("R"));
        request.Headers.Add("x-ms-version", "2020-07-15");
        request.Headers.Add("Authorization", "type%3dmaster%26ver%3d1.0%26sig%3dfake"); // auth is disabled
        request.Headers.Add("x-ms-cosmos-is-query-plan-request", "True");
        request.Headers.Add("x-ms-cosmos-query-version", "1.4");
        request.Headers.Add("x-ms-cosmos-supported-query-features",
            "NonValueAggregate, Aggregate, Distinct, MultipleOrderBy, OffsetAndLimit, OrderBy, Top, CompositeAggregate, GroupBy, MultipleAggregates");

        var response = await http.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var plan = JsonDocument.Parse(body).RootElement;

        // Verify the plan has the expected structure
        plan.TryGetProperty("partitionedQueryExecutionInfoVersion", out _).ShouldBeTrue();
        plan.TryGetProperty("queryInfo", out var queryInfo).ShouldBeTrue();
        plan.TryGetProperty("queryRanges", out var queryRanges).ShouldBeTrue();

        queryRanges.GetArrayLength().ShouldBeGreaterThan(0);
        queryInfo.TryGetProperty("rewrittenQuery", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task QueryPlanRequest_ShouldNotReturnMissingIdError()
    {
        // This is the exact scenario that failed in production:
        // POST /docs with query plan header but no 'id' in body
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _fixture.Client.CreateDatabaseAsync(dbName)).Database;
        var container = (await db.CreateContainerAsync($"qp2-{Guid.NewGuid():N}"[..20], "/pk")).Container;

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var http = new HttpClient(handler);

        // This body has NO 'id' property — old SDKs send query text only
        var requestBody = JsonSerializer.Serialize(new
        {
            query = "SELECT * FROM c WHERE c.name = @name",
            parameters = new[] { new { name = "@name", value = "test" } }
        });

        var baseUrl = _fixture.Client.Endpoint.ToString().TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl}/dbs/{dbName}/colls/{container.Id}/docs")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/query+json")
        };

        request.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("R"));
        request.Headers.Add("x-ms-version", "2020-07-15");
        request.Headers.Add("Authorization", "type%3dmaster%26ver%3d1.0%26sig%3dfake");
        request.Headers.Add("x-ms-cosmos-is-query-plan-request", "True");

        var response = await http.SendAsync(request);

        // Should NOT return 400 "Missing 'id' property"
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("Missing 'id' property");
    }
}

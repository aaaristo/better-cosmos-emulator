namespace Cosmos.Emulator.Api.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", GetAccount);
    }

    private static IResult GetAccount(HttpContext context)
    {
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        return Results.Json(new
        {
            _self = "",
            id = "localhost",
            _rid = "localhost",
            media = "//media/",
            addresses = "",
            _dbs = "//dbs/",
            writableLocations = new[]
            {
                new { name = "South Central US", databaseAccountEndpoint = baseUrl }
            },
            readableLocations = new[]
            {
                new { name = "South Central US", databaseAccountEndpoint = baseUrl }
            },
            enableMultipleWriteLocations = false,
            userReplicationPolicy = new
            {
                asyncReplication = false,
                minReplicaSetSize = 1,
                maxReplicaSetSize = 4
            },
            userConsistencyPolicy = new
            {
                defaultConsistencyLevel = "Session"
            },
            systemReplicationPolicy = new
            {
                minReplicaSetSize = 1,
                maxReplicaSetSize = 4
            },
            readPolicy = new { primaryReadCoefficient = 1, secondaryReadCoefficient = 1 },
            queryEngineConfiguration = "{\"maxSqlQueryInputLength\":262144,\"maxJoinsPerSqlQuery\":5,\"maxLogicalAndPerSqlQuery\":500,\"maxLogicalOrPerSqlQuery\":500,\"maxUdfRefPerSqlQuery\":10,\"maxInExpressionItemsCount\":16000,\"queryMaxInMemorySortDocumentCount\":500,\"maxQueryRequestTimeoutFraction\":0.9,\"sqlAllowNonFiniteNumbers\":false,\"sqlAllowAggregateFunctions\":true,\"sqlAllowSubQuery\":true,\"sqlAllowScalarSubQuery\":true,\"allowNewKeywords\":true,\"sqlAllowLike\":true,\"sqlAllowGroupByClause\":true,\"maxSpatialQueryCells\":12,\"spatialMaxGeometryPointCount\":256,\"sqlDisableOptimizationFlags\":0,\"sqlAllowTop\":true,\"enableSpatialIndexing\":true}"
        });
    }
}

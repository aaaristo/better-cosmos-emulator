using System.Reflection;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

public class PkRangeDeserializationTest
{
    [Fact]
    public void DeserializePkRangeResponse_ShouldProduceValidRoutingMap()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "pkrange-debug.log");
        void Log(string s) { File.AppendAllText(logPath, s + "\n"); }

        var json = """{"_rid":"AAARuIAAEbkCAAAAAAAAUA==","id":"0","_etag":"\"00000000-0000-0000-bc3a-4292ecd0bcb7\"","minInclusive":"","maxExclusive":"FF","ridPrefix":0,"_self":"dbs/AAARuA==/colls/AAARuIAAEbk=/pkranges/AAARuIAAEbkCAAAAAAAAUA==/","throughputFraction":1,"status":"online","parents":[],"ownedArchivalPKRangeIds":[],"_ts":1775485335,"lsn":1}""";

        var pkRangeType = Type.GetType("Microsoft.Azure.Documents.PartitionKeyRange, Microsoft.Azure.Cosmos.Direct")!;
        var range = Newtonsoft.Json.JsonConvert.DeserializeObject(json, pkRangeType)!;

        // Use NonPublic since MinInclusive is internal
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var min = pkRangeType.GetProperty("MinInclusive", flags)?.GetValue(range) as string;
        var max = pkRangeType.GetProperty("MaxExclusive", flags)?.GetValue(range) as string;
        var id = pkRangeType.GetProperty("Id", flags)?.GetValue(range) as string;
        var rid = pkRangeType.GetProperty("ResourceId", flags)?.GetValue(range) as string;

        Log($"MinInclusive: '{min}' (null={min is null})");
        Log($"MaxExclusive: '{max}' (null={max is null})");
        Log($"Id: '{id}'");
        Log($"ResourceId: '{rid}'");

        // Check PartitionKeyInternal constants
        var pkiType = Type.GetType("Microsoft.Azure.Documents.Routing.PartitionKeyInternal, Microsoft.Azure.Cosmos.Direct")!;
        var minConst = pkiType.GetField("MinimumInclusiveEffectivePartitionKey", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;
        var maxConst = pkiType.GetField("MaximumExclusiveEffectivePartitionKey", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;
        Log($"SDK MinimumInclusiveEffectivePartitionKey: '{minConst}'");
        Log($"SDK MaximumExclusiveEffectivePartitionKey: '{maxConst}'");
        Log($"Min matches: {min == minConst}");
        Log($"Max matches: {max == maxConst}");

        // Try building routing map
        if (min is not null && max is not null)
        {
            var routingMapType = Type.GetType("Microsoft.Azure.Cosmos.Routing.CollectionRoutingMap, Microsoft.Azure.Cosmos.Client")!;
            var tryCreate = routingMapType.GetMethod("TryCreateCompleteRoutingMap", BindingFlags.Public | BindingFlags.Static)!;
            var serviceIdentityType = Type.GetType("Microsoft.Azure.Documents.ServiceIdentity, Microsoft.Azure.Cosmos.Direct")!;
            var tupleType = typeof(Tuple<,>).MakeGenericType(pkRangeType, serviceIdentityType);
            var tuple = Activator.CreateInstance(tupleType, range, null);
            var listType = typeof(List<>).MakeGenericType(tupleType);
            var list = Activator.CreateInstance(listType)!;
            listType.GetMethod("Add")!.Invoke(list, new[] { tuple });

            var result = tryCreate.Invoke(null, new object?[] { list, "test", false, null });
            Log($"TryCreateCompleteRoutingMap: {(result is null ? "NULL (FAIL)" : "SUCCESS")}");
        }
        else
        {
            Log("Skipping routing map test because min/max is null");
        }

        min.ShouldNotBeNull("MinInclusive is null");
    }
}

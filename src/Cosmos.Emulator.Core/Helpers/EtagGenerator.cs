namespace Cosmos.Emulator.Core.Helpers;

public static class EtagGenerator
{
    public static string Generate()
    {
        // Match official emulator format: "00000000-0000-0000-xxxx-xxxxxxxxxxxx"
        var g = Guid.NewGuid().ToString(); // xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
        // Take the last two groups (positions 19-22 and 24-35)
        return $"\"00000000-0000-0000-{g[19..23]}-{g[24..]}\"";
    }
}

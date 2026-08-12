using Cosmos.Emulator.Core.Models;
using Microsoft.Data.Sqlite;

namespace Cosmos.Emulator.Storage;

/// <summary>
/// Scopes a read to an effective partition key range, which is how the SDK asks
/// for a prefix of a hierarchical partition key (it hashes the prefix client-side
/// and sends the range rather than the key itself).
/// </summary>
/// <remarks>
/// The hash is applied through a SQLite application-defined function rather than a
/// stored column, so it can never fall out of sync with <c>partition_key</c> and
/// no existing database file needs migrating. That costs a scan, but only on reads
/// that actually carry a narrowed range — <see cref="Cosmos.Emulator.Core.Helpers.EffectivePartitionKey.IsFullRange"/>
/// filters out the container-wide range the SDK sends on ordinary reads.
/// </remarks>
public static class EpkFilter
{
    public const string FunctionName = "cosmos_epk";

    /// <summary>
    /// Renders the predicate scoping <paramref name="column"/> to an EPK range,
    /// binding to parameters derived from <paramref name="paramPrefix"/>.
    /// </summary>
    public static string BuildSql(string column, string paramPrefix) =>
        $"({FunctionName}({column}) >= {paramPrefix}_start AND " +
        $"{FunctionName}({column}) < {paramPrefix}_end)";

    /// <summary>Binds the two literals compared by <see cref="BuildSql"/>.</summary>
    public static void Bind(SqliteCommand cmd, string paramPrefix, EpkRange range)
    {
        cmd.Parameters.AddWithValue($"{paramPrefix}_start", range.StartInclusive);
        cmd.Parameters.AddWithValue($"{paramPrefix}_end", range.EndExclusive);
    }
}

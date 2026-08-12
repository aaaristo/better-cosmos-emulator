namespace Cosmos.Emulator.Core.Helpers;

/// <summary>
/// Builds the SQL bounds used to match rows by partition key, including the
/// hierarchical (sub-partitioned) case where the caller supplies only a prefix
/// of the key components — e.g. <c>["tenant1"]</c> against a container
/// partitioned by <c>/tenantId, /userId</c>.
/// </summary>
/// <remarks>
/// Partition keys are stored as the canonical JSON array produced by
/// <see cref="PartitionKeyExtractor"/> — no whitespace, elements in path order,
/// e.g. <c>["tenant1","user1"]</c>. Matching a prefix therefore means matching
/// every stored key that begins with the prefix's elements and continues with
/// either <c>,</c> (a deeper key) or <c>]</c> (the exact same key).
///
/// That splits into an equality probe plus one contiguous range, both of which
/// SQLite can serve from the <c>partition_key</c> index under its default
/// BINARY collation:
///
/// <code>
///   prefix ["a"]  →  open = '["a"'
///                    exact = '["a"]'
///                    low   = '["a",'   high = '["a"-'   (',' is 0x2C, '-' is 0x2D)
/// </code>
///
/// The open form ends on the delimiter that closes the last element (a quote for
/// strings, a digit for numbers), so a longer *value* can never be mistaken for a
/// longer *key*: <c>["ab","c"]</c> and <c>[12]</c> both sort outside the range
/// computed for <c>["a"]</c> and <c>[1]</c> respectively.
/// </remarks>
public static class PartitionKeyPredicate
{
    /// <summary>
    /// The three literals a partition key match compares against: the key itself,
    /// and the half-open range covering every key nested beneath it.
    /// </summary>
    public readonly record struct Bounds(string Exact, string RangeLow, string RangeHigh);

    /// <summary>
    /// Computes the match bounds for a canonical partition key array.
    /// </summary>
    public static Bounds Compute(string partitionKey)
    {
        if (!partitionKey.EndsWith(']'))
            throw new ArgumentException(
                $"Partition key must be a canonical JSON array, got '{partitionKey}'.",
                nameof(partitionKey));

        // Everything up to (but not including) the closing bracket.
        var open = partitionKey[..^1];

        return new Bounds(
            Exact: partitionKey,
            RangeLow: open + ",",
            RangeHigh: open + "-");
    }

    /// <summary>
    /// Renders the SQL predicate matching <paramref name="column"/> against a
    /// partition key or key prefix, binding the three literals to parameters
    /// derived from <paramref name="paramPrefix"/>.
    /// </summary>
    public static string BuildSql(string column, string paramPrefix) =>
        $"({column} = {paramPrefix}_exact OR " +
        $"({column} >= {paramPrefix}_lo AND {column} < {paramPrefix}_hi))";
}

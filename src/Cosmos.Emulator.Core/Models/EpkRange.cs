namespace Cosmos.Emulator.Core.Models;

/// <summary>
/// A half-open range over effective partition key hashes, as sent by the SDK in
/// <c>x-ms-start-epk</c> / <c>x-ms-end-epk</c> to scope a read to part of a
/// partition key range — most importantly, to a prefix of a hierarchical key.
/// </summary>
/// <param name="StartInclusive">Lower bound, compared ordinally. Included.</param>
/// <param name="EndExclusive">Upper bound, compared ordinally. Excluded.</param>
public readonly record struct EpkRange(string StartInclusive, string EndExclusive);

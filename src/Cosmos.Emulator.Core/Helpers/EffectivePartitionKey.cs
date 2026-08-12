using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Cosmos.Emulator.Core.Helpers;

/// <summary>
/// Computes the Cosmos DB <em>effective partition key</em> (EPK) — the hash the
/// service routes on — for a canonical partition key array.
/// </summary>
/// <remarks>
/// This must be bit-for-bit identical to the SDK's computation, because the two
/// sides never compare notes: for a <b>partial</b> (prefix) partition key the SDK
/// does not send <c>x-ms-documentdb-partitionkey</c> at all. It hashes the prefix
/// itself and sends a range instead —
/// <c>x-ms-start-epk: &lt;hash&gt;</c> / <c>x-ms-end-epk: &lt;hash&gt;FF</c> — so the
/// emulator can only honour it by hashing each document's key the same way and
/// comparing.
///
/// The algorithm is partition key definition version 2 (the default, and the only
/// version hierarchical containers support): each component is hashed on its own
/// with MurmurHash3 x64 128 and the per-component hashes are concatenated. That
/// concatenation is what makes prefix routing work — the EPK of
/// <c>["t1","u1"]</c> literally starts with the EPK of <c>["t1"]</c>, so a prefix
/// scope becomes a contiguous range over hex strings.
///
/// Verified against <c>ThinClientTransportSerializer.GetEffectivePartitionKeyHash</c>
/// in Microsoft.Azure.Cosmos.Direct; see the golden vectors in the unit tests.
/// </remarks>
public static class EffectivePartitionKey
{
    /// <summary>
    /// Leading byte identifying a component's type, written before its payload.
    /// Mirrors <c>Microsoft.Azure.Documents.Routing.PartitionKeyComponentType</c>.
    /// </summary>
    private enum ComponentType : byte
    {
        Undefined = 0x00,
        Null = 0x01,
        False = 0x02,
        True = 0x03,
        Number = 0x05,
        String = 0x08,
    }

    /// <summary>Terminates a string payload so "ab" cannot hash as a prefix of "abc".</summary>
    private const byte StringTerminator = 0xFF;

    /// <summary>
    /// The widest EPK the service will route to — every real hash sorts below it
    /// because the top two bits of the first byte are cleared.
    /// </summary>
    public const string MaxExclusive = "FF";

    /// <summary>
    /// Hashes a canonical partition key array (e.g. <c>["t1","u1"]</c>) into its
    /// effective partition key, as uppercase hex.
    /// </summary>
    public static string Compute(string partitionKeyJson)
    {
        using var doc = JsonDocument.Parse(partitionKeyJson);
        var array = doc.RootElement;

        if (array.ValueKind != JsonValueKind.Array)
            throw new ArgumentException(
                $"Partition key must be a JSON array, got '{partitionKeyJson}'.",
                nameof(partitionKeyJson));

        var sb = new StringBuilder(array.GetArrayLength() * 32);
        foreach (var component in array.EnumerateArray())
            sb.Append(HashComponent(component));

        return sb.ToString();
    }

    /// <summary>
    /// Whether an EPK range covers the whole hash space, in which case filtering by
    /// it is a no-op and callers can skip hashing entirely.
    /// </summary>
    public static bool IsFullRange(string? startInclusive, string? endExclusive) =>
        string.IsNullOrEmpty(startInclusive) &&
        (string.IsNullOrEmpty(endExclusive) ||
         string.Equals(endExclusive, MaxExclusive, StringComparison.OrdinalIgnoreCase));

    private static string HashComponent(JsonElement component)
    {
        var payload = Encode(component);
        var (low, high) = MurmurHash3X64128(payload);

        // The service reserves the top two bits of the leading byte, so every EPK
        // sorts below the "FF" upper bound the routing map uses.
        high &= 0x3FFF_FFFF_FFFF_FFFFUL;

        // Big-endian: the hash's high half first, so hex ordering matches hash ordering.
        return high.ToString("X16") + low.ToString("X16");
    }

    private static byte[] Encode(JsonElement component)
    {
        switch (component.ValueKind)
        {
            case JsonValueKind.String:
            {
                var text = component.GetString()!;
                var byteCount = Encoding.UTF8.GetByteCount(text);
                var buffer = new byte[byteCount + 2];
                buffer[0] = (byte)ComponentType.String;
                Encoding.UTF8.GetBytes(text, buffer.AsSpan(1));
                buffer[^1] = StringTerminator;
                return buffer;
            }

            case JsonValueKind.Number:
            {
                var buffer = new byte[9];
                buffer[0] = (byte)ComponentType.Number;
                BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(1), component.GetDouble());
                return buffer;
            }

            case JsonValueKind.True:
                return [(byte)ComponentType.True];

            case JsonValueKind.False:
                return [(byte)ComponentType.False];

            case JsonValueKind.Null:
                return [(byte)ComponentType.Null];

            // PartitionKeyExtractor writes '{}' for a path the document doesn't have.
            default:
                return [(byte)ComponentType.Undefined];
        }
    }

    /// <summary>
    /// MurmurHash3, 128-bit x64 variant, seed 0 — returned as its two 64-bit halves.
    /// </summary>
    private static (ulong Low, ulong High) MurmurHash3X64128(ReadOnlySpan<byte> data)
    {
        const ulong c1 = 0x87C3_7B91_1142_53D5UL;
        const ulong c2 = 0x4CF5_AD43_2745_937FUL;

        ulong h1 = 0;
        ulong h2 = 0;

        var blockCount = data.Length / 16;
        for (var i = 0; i < blockCount; i++)
        {
            var block = data.Slice(i * 16, 16);
            var k1 = BinaryPrimitives.ReadUInt64LittleEndian(block);
            var k2 = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);

            k1 *= c1;
            k1 = BitOperations.RotateLeft(k1, 31);
            k1 *= c2;
            h1 ^= k1;

            h1 = BitOperations.RotateLeft(h1, 27);
            h1 += h2;
            h1 = h1 * 5 + 0x52DC_E729;

            k2 *= c2;
            k2 = BitOperations.RotateLeft(k2, 33);
            k2 *= c1;
            h2 ^= k2;

            h2 = BitOperations.RotateLeft(h2, 31);
            h2 += h1;
            h2 = h2 * 5 + 0x3849_5AB5;
        }

        var tail = data[(blockCount * 16)..];
        ulong t1 = 0;
        ulong t2 = 0;

        // Deliberately cascading: each case also contributes every lower byte.
        switch (tail.Length)
        {
            case 15: t2 ^= (ulong)tail[14] << 48; goto case 14;
            case 14: t2 ^= (ulong)tail[13] << 40; goto case 13;
            case 13: t2 ^= (ulong)tail[12] << 32; goto case 12;
            case 12: t2 ^= (ulong)tail[11] << 24; goto case 11;
            case 11: t2 ^= (ulong)tail[10] << 16; goto case 10;
            case 10: t2 ^= (ulong)tail[9] << 8; goto case 9;
            case 9:
                t2 ^= tail[8];
                t2 *= c2;
                t2 = BitOperations.RotateLeft(t2, 33);
                t2 *= c1;
                h2 ^= t2;
                goto case 8;
            case 8: t1 ^= (ulong)tail[7] << 56; goto case 7;
            case 7: t1 ^= (ulong)tail[6] << 48; goto case 6;
            case 6: t1 ^= (ulong)tail[5] << 40; goto case 5;
            case 5: t1 ^= (ulong)tail[4] << 32; goto case 4;
            case 4: t1 ^= (ulong)tail[3] << 24; goto case 3;
            case 3: t1 ^= (ulong)tail[2] << 16; goto case 2;
            case 2: t1 ^= (ulong)tail[1] << 8; goto case 1;
            case 1:
                t1 ^= tail[0];
                t1 *= c1;
                t1 = BitOperations.RotateLeft(t1, 31);
                t1 *= c2;
                h1 ^= t1;
                break;
        }

        h1 ^= (ulong)data.Length;
        h2 ^= (ulong)data.Length;
        h1 += h2;
        h2 += h1;
        h1 = Mix(h1);
        h2 = Mix(h2);
        h1 += h2;
        h2 += h1;

        return (h1, h2);
    }

    private static ulong Mix(ulong k)
    {
        k ^= k >> 33;
        k *= 0xFF51_AFD7_ED55_8CCDUL;
        k ^= k >> 33;
        k *= 0xC4CE_B9FE_1A85_EC53UL;
        k ^= k >> 33;
        return k;
    }
}

using System.Buffers.Binary;

namespace Cosmos.Emulator.Core.Helpers;

/// <summary>
/// Generates resource IDs matching the Cosmos DB SDK's ResourceId binary format.
/// Verified against the official emulator output:
///   Database:    4 bytes  (e.g. e7835400 → "54NUAA==")
///   Collection:  8 bytes  (db[4] + coll[4], byte[4] bit7 set)
///   Document:   16 bytes  (db[4] + coll[4] + doc[8], byte[15] upper nibble = type)
///   PK Range:   16 bytes  (db[4] + coll[4] + pkr[8], byte[15] upper nibble = 0x5)
/// </summary>
public static class ResourceIdGenerator
{
    private static uint _counter;

    static ResourceIdGenerator()
    {
        _counter = (uint)Random.Shared.Next(256, 65535);
    }

    public static string GenerateDatabaseRid()
    {
        // 4 bytes: [dbId(4)] — matches e.g. "54NUAA=="
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, NextId());
        // Official emulator seems to zero-pad last byte sometimes; not required
        return Encode(bytes);
    }

    public static string GenerateCollectionRid(string databaseRid)
    {
        // 8 bytes: [dbId(4) + collId(4)] — byte[4] must have bit 7 set
        var dbBytes = Decode(databaseRid);
        var bytes = new byte[8];
        Buffer.BlockCopy(dbBytes, 0, bytes, 0, Math.Min(dbBytes.Length, 4));

        var collId = NextId();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), collId);
        bytes[4] |= 0x80; // Mark as collection (vs user)

        return Encode(bytes);
    }

    public static string GenerateDocumentRid(string collectionRid)
    {
        // 16 bytes: [dbId(4) + collId(4) + docId(8)]
        // byte[15] upper nibble = 0x0 (document type)
        var collBytes = Decode(collectionRid);
        var bytes = new byte[16];
        Buffer.BlockCopy(collBytes, 0, bytes, 0, Math.Min(collBytes.Length, 8));

        var id1 = NextId();
        var id2 = NextId();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), id1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), id2);
        bytes[15] &= 0x0F; // Ensure upper nibble = 0 (document)

        return Encode(bytes);
    }

    public static string GeneratePkRangeRid(string collectionRid)
    {
        // 16 bytes: collRid[8] + [02 00 00 00 00 00 00 50]
        // Byte 8 = 0x02, Byte 15 upper nibble = 0x5 (PKRange type)
        // This matches the official emulator's pattern exactly
        var collBytes = Decode(collectionRid);
        var bytes = new byte[16];
        Buffer.BlockCopy(collBytes, 0, bytes, 0, Math.Min(collBytes.Length, 8));

        bytes[8] = 0x02;  // Fixed value matching official emulator
        // bytes[9..14] = 0x00 (already zero)
        bytes[15] = 0x50; // Upper nibble = 5 (PKRange type)

        return Encode(bytes);
    }

    private static uint NextId() => Interlocked.Increment(ref _counter);

    private static string Encode(byte[] bytes)
    {
        // Official emulator includes padding (=) in rids
        return Convert.ToBase64String(bytes);
    }

    private static byte[] Decode(string rid)
    {
        rid = rid.Replace("-", "/");
        int pad = (4 - rid.Length % 4) % 4;
        return Convert.FromBase64String(rid + new string('=', pad));
    }
}

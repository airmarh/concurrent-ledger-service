using System.Security.Cryptography;

namespace NovaWallet.Domain;

// RFC 9562 UUIDv7: a 48-bit millisecond timestamp in the high bits followed by
// random bits, so IDs generated close together sort close together — avoiding the
// B-tree index fragmentation Guid.NewGuid()'s fully-random UUIDv4 causes at scale,
// while keeping GUID's client-side generation and non-enumerable properties.
public static class UuidV7
{
    public static Guid NewId()
    {
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> bytes = stackalloc byte[16];

        bytes[0] = (byte)(timestampMs >> 40);
        bytes[1] = (byte)(timestampMs >> 32);
        bytes[2] = (byte)(timestampMs >> 24);
        bytes[3] = (byte)(timestampMs >> 16);
        bytes[4] = (byte)(timestampMs >> 8);
        bytes[5] = (byte)timestampMs;

        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        random.CopyTo(bytes[6..]);

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant

        return new Guid(bytes, bigEndian: true);
    }
}

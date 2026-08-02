using System.Security.Cryptography;
using System.Text;

namespace Auxilia.PlatformData;

/// <summary>
/// Derives a stable <see cref="Guid"/> (UUIDv5-style, SHA-1 based) from natural-key strings so
/// that upserts from any replica converge on the same record ID.
/// </summary>
public static class DeterministicGuid
{
    private static readonly Guid Namespace = new("8f1d9a52-3f43-4c11-9e26-7a3b1c5d0e44");

    public static Guid For(params string[] keyParts)
    {
        ArgumentNullException.ThrowIfNull(keyParts);
        if (keyParts.Length == 0)
            throw new ArgumentException("At least one key part is required.", nameof(keyParts));

        var name = Encoding.UTF8.GetBytes(string.Join("", keyParts));
        var namespaceBytes = Namespace.ToByteArray();
        SwapGuidByteOrder(namespaceBytes);

        var hash = SHA1.HashData([.. namespaceBytes, .. name]);

        var result = new byte[16];
        Array.Copy(hash, result, 16);
        result[6] = (byte)((result[6] & 0x0F) | 0x50); // version 5
        result[8] = (byte)((result[8] & 0x3F) | 0x80); // RFC 4122 variant
        SwapGuidByteOrder(result);
        return new Guid(result);
    }

    // Guid stores the first three components little-endian; RFC 4122 hashing is big-endian.
    private static void SwapGuidByteOrder(byte[] guidBytes)
    {
        (guidBytes[0], guidBytes[3]) = (guidBytes[3], guidBytes[0]);
        (guidBytes[1], guidBytes[2]) = (guidBytes[2], guidBytes[1]);
        (guidBytes[4], guidBytes[5]) = (guidBytes[5], guidBytes[4]);
        (guidBytes[6], guidBytes[7]) = (guidBytes[7], guidBytes[6]);
    }
}

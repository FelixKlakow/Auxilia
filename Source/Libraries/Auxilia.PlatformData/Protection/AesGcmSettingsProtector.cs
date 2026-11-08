using System.Security.Cryptography;
using System.Text;

namespace Auxilia.PlatformData.Protection;

/// <summary>
/// AES-GCM protection: <c>enc1:</c> + Base64(nonce | ciphertext | tag) for settings, and
/// <c>enb1</c> | nonce | ciphertext | tag for binary content.
/// </summary>
public sealed class AesGcmSettingsProtector(byte[] key) : ISettingsProtector
{
    private const string Prefix = "enc1:";
    private static readonly byte[] BinaryMagic = "enb1"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public string Protect(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        return Prefix + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Value is not in the expected enc1 format.");

        var blob = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        var nonce = blob.AsSpan(0, NonceSize);
        var cipher = blob.AsSpan(NonceSize, blob.Length - NonceSize - TagSize);
        var tag = blob.AsSpan(blob.Length - TagSize, TagSize);

        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public byte[] ProtectBytes(ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[BinaryMagic.Length + NonceSize + plaintext.Length + TagSize];
        BinaryMagic.CopyTo(output, 0);
        var nonce = output.AsSpan(BinaryMagic.Length, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        var cipher = output.AsSpan(BinaryMagic.Length + NonceSize, plaintext.Length);
        var tag = output.AsSpan(output.Length - TagSize, TagSize);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        return output;
    }

    public byte[] UnprotectBytes(ReadOnlySpan<byte> protectedValue)
    {
        if (protectedValue.Length < BinaryMagic.Length + NonceSize + TagSize
            || !protectedValue[..BinaryMagic.Length].SequenceEqual(BinaryMagic))
            throw new CryptographicException("Content is not in the expected enb1 format.");

        var nonce = protectedValue.Slice(BinaryMagic.Length, NonceSize);
        var cipher = protectedValue[(BinaryMagic.Length + NonceSize)..^TagSize];
        var tag = protectedValue[^TagSize..];

        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}

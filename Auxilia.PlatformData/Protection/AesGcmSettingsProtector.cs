using System.Security.Cryptography;
using System.Text;

namespace Auxilia.PlatformData.Protection;

/// <summary>
/// AES-GCM protection: <c>enc1:</c> + Base64(nonce | ciphertext | tag).
/// </summary>
public sealed class AesGcmSettingsProtector(byte[] key) : ISettingsProtector
{
    private const string Prefix = "enc1:";
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
}

using System.Security.Cryptography;
using System.Text;

namespace Auxilia.Workflows.Crypto;

public sealed class EphemeralKeyPair : IDisposable
{
    private readonly RSA _rsa;

    public EphemeralKeyPair()
    {
        _rsa = RSA.Create(4096);
    }

    public string PublicKeyBase64
        => Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());

    public string Decrypt(string base64Ciphertext)
    {
        var cipherBytes = Convert.FromBase64String(base64Ciphertext);
        var plainBytes = _rsa.Decrypt(cipherBytes, RSAEncryptionPadding.OaepSHA256);
        return Encoding.UTF8.GetString(plainBytes);
    }

    public void Dispose() => _rsa.Dispose();
}

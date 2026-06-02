using System.Security.Cryptography;

namespace Auxilia.Workflows.Packer;

public sealed class RsaFileSigningProvider : IDisposable
{
    private readonly RSA _rsa;

    public RsaFileSigningProvider(string pemKeyFilePath)
    {
        if (!File.Exists(pemKeyFilePath))
            throw new FileNotFoundException("PEM key file not found.", pemKeyFilePath);

        var pemText = File.ReadAllText(pemKeyFilePath);
        _rsa = RSA.Create();
        _rsa.ImportFromPem(pemText);
    }

    public byte[] Sign(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return _rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    public string PublicKeyBase64 => Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());

    public void Dispose() => _rsa.Dispose();
}

namespace Auxilia.Workflows.Crypto;

public interface ISigningProvider
{
    byte[] Sign(byte[] data);
}

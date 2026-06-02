namespace Auxilia.Workflows.Crypto;

public interface IWorkflowPackageVerifier
{
    bool Verify(Stream zipStream);
}

namespace Auxilia.Workflows;

public interface IPluginManifestVerifier
{
    bool Verify(PluginManifest manifest, byte[] assemblyBytes);
}

namespace Auxilia.Workflows.Crypto;

public sealed record WorkflowPackageManifest(
    IReadOnlyList<WorkflowPackageFileEntry> Files,
    string SignatureBase64,
    string PublicKeyBase64);

using System.Text.Json.Serialization;

namespace Auxilia.SteeringInstance.Workflows.Storage;

/// <summary>
/// Manifest embedded in a <c>*.workflow.zip</c> package. Records the workflow type,
/// the relative path of the executable entry, and the cryptographic proof that the
/// package was signed by a trusted key.
/// </summary>
public sealed record WorkflowPackageManifest(
    /// <summary>Workflow type name — must match <see cref="RunWorkflowCommand.WorkflowType"/>.</summary>
    [property: JsonPropertyName("workflowType")] string WorkflowType,
    /// <summary>
    /// Path of the workflow executable relative to the ZIP root (e.g. <c>my-workflow</c> or
    /// <c>bin/my-workflow.dll</c>).  The launcher prepends <c>/workflow/</c> to produce the
    /// container <c>Cmd</c> entry.
    /// </summary>
    [property: JsonPropertyName("executableRelativePath")] string ExecutableRelativePath,
    /// <summary>Base64-encoded SHA-256 hash of the ZIP content (excluding the signature entry).</summary>
    [property: JsonPropertyName("contentHashBase64")] string ContentHashBase64,
    /// <summary>Base64-encoded RSA-PSS signature over <see cref="ContentHashBase64"/>.</summary>
    [property: JsonPropertyName("signatureBase64")] string SignatureBase64,
    /// <summary>Base64-encoded SubjectPublicKeyInfo of the signing key.</summary>
    [property: JsonPropertyName("publicKeyBase64")] string PublicKeyBase64);

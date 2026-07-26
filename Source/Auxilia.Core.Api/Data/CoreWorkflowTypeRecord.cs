using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// One permanently registered workflow type in the Core's registry (ARCHITECTURE §7): the signed
/// package coordinate, the trust status, and the type's schema. Registration is deploy-time and
/// survives until the type is unregistered; only Active types are dispatchable. The schema is
/// refreshed from the runner's <see cref="Auxilia.Workflows.Messaging.Messages.WorkflowSchemaPublished"/>
/// bus events — but only for types that are registered here.
/// </summary>
public sealed record CoreWorkflowTypeRecord : IEntity
{
    public Guid Id { get; init; }
    public required string WorkflowType { get; init; }

    /// <summary>The package coordinate the runner loads (docker://, https://, or core:// for a stored package).</summary>
    public string? PackageUri { get; init; }

    /// <summary>The type's schema (serialized <c>WorkflowSchema</c>); null until known.</summary>
    public string? SchemaJson { get; init; }

    /// <summary>Pending / Active / Denied (see <c>WorkflowTypeStatus</c>).</summary>
    public required string Status { get; init; }

    /// <summary>Why the type is in its status (e.g. the denial reason, or how it was trusted).</summary>
    public string? StatusReason { get; init; }

    /// <summary>SPKI public key (base64) the package manifest is signed with; null for docker packages.</summary>
    public string? PublisherKeyBase64 { get; init; }

    /// <summary>Absolute path of the package stored Core-side (the transfer-to-sign path); null when external.</summary>
    public string? StoredPackagePath { get; init; }

    public Guid? RegisteredBy { get; init; }
    public DateTimeOffset RegisteredUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }

    public static Guid IdFor(string workflowType) => DeterministicGuid.For("core-workflow-type", workflowType);
}

namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Registers (or updates) a workflow package in the platform's workflow registry via the
/// <c>slot-configurations</c> seeding exchange. Deployment tooling sends this when a signed
/// package is rolled out so configuration editors can offer the workflow before its first run;
/// <see cref="SchemaJson"/> optionally carries the package's <c>workflow-schema.json</c>
/// (e.g. captured via <c>--emit-schema</c>) so slot declarations are known up front.
/// </summary>
public sealed record RegisterWorkflowPackageCommand(
    string WorkflowType,
    string PackageUri,
    string? DisplayName = null,
    string? Version = null,
    string? SchemaJson = null);

/// <summary>Removes a workflow package from the registry (configurations referencing it keep working).</summary>
public sealed record RemoveWorkflowPackageCommand(string WorkflowType);

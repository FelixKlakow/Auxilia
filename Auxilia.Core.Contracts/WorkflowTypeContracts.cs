namespace Auxilia.Core.Contracts;

/// <summary>Lifecycle states of a registered workflow type in the Core registry.</summary>
public static class WorkflowTypeStatus
{
    /// <summary>Registered but awaiting the signing authority's decision — not runnable.</summary>
    public const string Pending = "Pending";

    /// <summary>Trusted (pre-signed by a trusted publisher, or approved) — runnable.</summary>
    public const string Active = "Active";

    /// <summary>The signing authority refused the package — not runnable; the reason is recorded.</summary>
    public const string Denied = "Denied";
}

/// <summary>
/// A workflow type in the Core registry. Types are registered permanently (until unregistered),
/// carry their signed package coordinate, and only <see cref="WorkflowTypeStatus.Active"/> types
/// can be run — a run references the type; the Core resolves the package.
/// </summary>
public sealed record WorkflowTypeDto(
    string WorkflowType,
    string Version,
    string Lifetime,
    string? Description,
    IReadOnlyList<string> Tags,
    string? PackageUri = null,
    string Status = WorkflowTypeStatus.Active);

/// <summary>Filter for listing registered workflow types.</summary>
public sealed record WorkflowTypeQuery(int Skip = 0, int Take = 50, string? Status = null);

/// <summary>
/// Register a workflow type into the Core registry. Exactly one package source: a
/// <see cref="PackageUri"/> (an https .workflow.zip the Core downloads and verifies, or a
/// docker:// image which carries no verifiable signature and therefore always needs approval), or
/// <see cref="PackageBase64"/> — the package transferred to the Core (the signing instance), which
/// stores it and serves it to the runner itself. A package signed by a trusted publisher key
/// activates immediately; anything else enters <see cref="WorkflowTypeStatus.Pending"/> until the
/// signing authority approves or denies.
/// </summary>
public sealed record RegisterWorkflowTypeRequest(
    string WorkflowType,
    string? PackageUri = null,
    string? PackageBase64 = null,
    string? SchemaJson = null);

/// <summary>Deny a pending workflow-type registration; the reason is recorded and returned to callers.</summary>
public sealed record DenyWorkflowTypeRequest(string Reason);

/// <summary>
/// The registry's administrative view of one workflow type: status, package coordinate, the
/// publisher key the package was signed with, and the registration audit trail.
/// </summary>
public sealed record WorkflowTypeRegistrationDto(
    string WorkflowType,
    string? PackageUri,
    string Status,
    string? StatusReason,
    string? PublisherKeyBase64,
    bool HasStoredPackage,
    Guid? RegisteredBy,
    DateTimeOffset RegisteredUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// One declared slot of a workflow: the config editor binds it to a connector. <see cref="Contract"/>
/// is the capability contract the slot expects (offer only matching connectors); <see cref="CapabilitiesJson"/>
/// carries the schema-declared capability requirement object as raw JSON. <see cref="ProviderTypes"/>
/// is the workflow's declared narrowing — when present, editors offer (and the Core admits) only
/// bindings of these provider types.
/// </summary>
public sealed record WorkflowSlotDto(
    string SlotName,
    string? Contract,
    string? Description,
    bool Optional,
    string? CapabilitiesJson,
    bool AllowMultiple = false,
    IReadOnlyList<string>? ProviderTypes = null);

/// <summary>One run input a workflow declares; the dispatch/config UI renders these generically.</summary>
public sealed record WorkflowInputDto(
    string Name,
    string Label,
    bool Required,
    string? Description,
    string Kind = InputKinds.Text,
    string? DefaultValue = null,
    IReadOnlyList<string>? Choices = null,
    IReadOnlyDictionary<string, string>? ChoiceLabels = null);

/// <summary>
/// Rendering kinds a declared input can carry — editors render inputs BY KIND, never by name.
/// An unknown kind renders as <see cref="Text"/> so old editors stay usable with newer schemas.
/// </summary>
public static class InputKinds
{
    public const string Text = "Text";
    public const string Multiline = "Multiline";
    public const string Boolean = "Boolean";
    /// <summary>One of <see cref="WorkflowInputDto.Choices"/>.</summary>
    public const string Choice = "Choice";
    public const string Number = "Number";
}

/// <summary>A schema-declared view the dashboard renders from these descriptors alone.
/// <see cref="DeclaredDataJson"/> is the view's optional packaging-time data (opaque JSON whose
/// meaning belongs to the renderer named by <see cref="RendererKey"/>) — e.g. the "flow" view's
/// declared steps, rendered as a stage view before any run exists.</summary>
public sealed record WorkflowViewDto(
    string Name,
    string Rendering,
    string Lifecycle,
    string? RendererKey,
    string ItemSchemaJson,
    string? DeclaredDataJson = null);

/// <summary>A trigger kind a workflow declares it can be started by (wired per configuration).</summary>
public sealed record WorkflowTriggerDto(string Kind, string? Description);

/// <summary>
/// The full schema of a registered workflow type — everything the config editor needs to build a
/// configuration: declared slots + their capability requirements, run inputs, views, trigger kinds,
/// consumed artifact types, environment requirements (raw JSON), and the interactive terminal port.
/// </summary>
public sealed record WorkflowSchemaDto(
    string WorkflowType,
    string Version,
    string SchemaVersion,
    string Lifetime,
    IReadOnlyList<string> Tags,
    IReadOnlyList<WorkflowSlotDto> Slots,
    IReadOnlyList<WorkflowInputDto> Inputs,
    IReadOnlyList<WorkflowViewDto> Views,
    IReadOnlyList<WorkflowTriggerDto> Triggers,
    IReadOnlyList<string> ConsumedArtifacts,
    int? InteractiveTerminalPort,
    string EnvironmentRequirementsJson,
    string? PackageUri = null,
    string Status = WorkflowTypeStatus.Active);

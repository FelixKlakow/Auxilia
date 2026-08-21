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

    /// <summary>Administratively switched off — not runnable (nor configurable) until re-enabled.
    /// Unlike <see cref="Denied"/> this is an operational act, not a trust decision.</summary>
    public const string Disabled = "Disabled";
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
/// Switch a registered type off (or back on) operationally: a disabled type cannot be run — every
/// configured workflow of it stops dispatching — and returns to Active when re-enabled.
/// </summary>
public sealed record SetWorkflowTypeEnabledRequest(bool Enabled);

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
/// One entry of a workflow type's Policy-Engine access list: <see cref="Action"/> (e.g.
/// <c>workflow.trigger</c>) granted to EXACTLY ONE subject — a role name, a principal, or a
/// first-class group. The FIRST entry for a (type, action) makes the list the EXCLUSIVE grant
/// source for that action on that type; no entries = the role-derived permission decides.
/// </summary>
public sealed record WorkflowTypeAccessEntryDto(
    string Action,
    string? RoleName = null,
    Guid? PrincipalId = null,
    Guid? GroupId = null);

/// <summary>Grants or revokes one workflow-type access-list entry (exactly one subject set).</summary>
public sealed record WorkflowTypeAccessChange(
    string Action,
    string? RoleName = null,
    Guid? PrincipalId = null,
    Guid? GroupId = null);

/// <summary>
/// One declared slot of a workflow: the config editor binds it to a connector. <see cref="Contract"/>
/// is the capability contract the slot expects (offer only matching connectors); <see cref="CapabilitiesJson"/>
/// carries the schema-declared capability requirement object as raw JSON. Providers declaring
/// required tools additionally match against the schema's <see cref="WorkflowSchemaDto.ProvidedTools"/>.
/// </summary>
public sealed record WorkflowSlotDto(
    string SlotName,
    string? Contract,
    string? Description,
    bool Optional,
    string? CapabilitiesJson,
    bool AllowMultiple = false);

/// <summary>One run input a workflow declares; the dispatch/config UI renders these generically.</summary>
public sealed record WorkflowInputDto(
    string Name,
    string Label,
    bool Required,
    string? Description,
    string Kind = InputKinds.Text,
    string? DefaultValue = null,
    IReadOnlyList<string>? Choices = null,
    IReadOnlyDictionary<string, string>? ChoiceLabels = null,
    bool PerRun = false);

/// <summary>
/// Rendering kinds a declared input can carry — editors render inputs BY KIND, never by name.
/// An unknown kind renders as <see cref="Text"/> so old editors stay usable with newer schemas.
/// The workflow SDK mirrors this vocabulary as <c>Auxilia.Workflows.WorkflowInputKinds</c> —
/// the two must stay identical (one wire vocabulary, declared and rendered).
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

/// <summary>An event type a workflow declares it publishes — the wiring points for event triggers.</summary>
public sealed record WorkflowEventDto(string EventType, string? PayloadSchemaJson, string? Description);

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
    string Status = WorkflowTypeStatus.Active)
{
    /// <summary>Event types the workflow declares it publishes.</summary>
    public IReadOnlyList<WorkflowEventDto> Events { get; init; } = [];

    /// <summary>Companion containers of the run's pod — the approval's spawn summary.</summary>
    public IReadOnlyList<WorkflowCompanionDto> Companions { get; init; } = [];

    /// <summary>
    /// The runtime pod-control envelope, when the workflow declares one: it may spawn
    /// companions at runtime from configuration-pinned catalog bases, up to its cap.
    /// </summary>
    public WorkflowPodControlDto? PodControl { get; init; }

    /// <summary>
    /// Hard cap of the pod topology: every declared companion's upper scale bound plus the
    /// pod-control envelope.
    /// </summary>
    public int MaxPodContainers { get; init; }

    /// <summary>
    /// Tool names the workflow's image bundles (open vocabulary, e.g. the CLIs its Dockerfile
    /// installs) — matched against each provider's <see cref="ProviderCatalogEntry.RequiredTools"/>:
    /// a provider is offerable and bindable only when every tool it requires is provided here.
    /// </summary>
    public IReadOnlyList<string> ProvidedTools { get; init; } = [];
}

/// <summary>The declared runtime-spawn envelope — part of the approval's spawn summary.</summary>
public sealed record WorkflowPodControlDto(
    int MaxContainers,
    string? Description,
    IReadOnlyList<string> PodVolumes);

/// <summary>
/// One declared companion container of a workflow's per-run pod: a digest-pinned inert
/// service the runner materializes on the run's private network. Rendered prominently in
/// the registry approval UI — approving the type IS the spawn grant.
/// </summary>
public sealed record WorkflowCompanionDto(
    string Name,
    string Image,
    int MinInstances,
    int MaxInstances,
    string? CountInput,
    IReadOnlyList<string> StartAfter,
    int? MemoryMb = null,
    double? Cpus = null);

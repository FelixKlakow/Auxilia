namespace Auxilia.Core.Contracts;

/// <summary>
/// A registered workflow type, as cataloged by the Core from the runner's schema registrations.
/// The config editor lists these to let an operator pick a workflow to configure.
/// </summary>
public sealed record WorkflowTypeDto(
    string WorkflowType,
    string Version,
    string Lifetime,
    string? Description,
    IReadOnlyList<string> Tags);

/// <summary>Filter for listing registered workflow types.</summary>
public sealed record WorkflowTypeQuery(int Skip = 0, int Take = 50);

/// <summary>
/// One declared slot of a workflow: the config editor binds it to a connector. <see cref="Contract"/>
/// is the capability contract the slot expects (offer only matching connectors); <see cref="CapabilitiesJson"/>
/// carries the schema-declared capability requirement object as raw JSON.
/// </summary>
public sealed record WorkflowSlotDto(
    string SlotName,
    string? Contract,
    string? Description,
    bool Optional,
    string? CapabilitiesJson);

/// <summary>One run input a workflow declares; the dispatch/config UI renders these generically.</summary>
public sealed record WorkflowInputDto(
    string Name,
    string Label,
    bool Required,
    string? Description);

/// <summary>A schema-declared view the dashboard renders from these descriptors alone.</summary>
public sealed record WorkflowViewDto(
    string Name,
    string Rendering,
    string Lifecycle,
    string? RendererKey,
    string ItemSchemaJson);

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
    string EnvironmentRequirementsJson);

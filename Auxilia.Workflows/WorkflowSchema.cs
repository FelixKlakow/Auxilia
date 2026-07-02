using Auxilia.Workflows.Environment;

namespace Auxilia.Workflows;

public sealed record WorkflowSchema(
    string WorkflowName,
    IReadOnlyList<SlotDefinition> Slots,
    IReadOnlyList<IEnvironmentRequirement> EnvironmentRequirements)
{
    public string SchemaVersion { get; init; } = "1.0";
    public string Version { get; init; } = string.Empty;
    public WorkflowLifetime Lifetime { get; init; } = WorkflowLifetime.OneShot;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<WorkflowOutputDescriptor> Outputs { get; init; } = [];
    public IReadOnlyList<SignalDescriptor> Signals { get; init; } = [];
    public IReadOnlyList<Views.ViewDescriptor> Views { get; init; } = [];
    public IReadOnlyList<Network.NetworkEndpointDeclaration> NetworkEndpoints { get; init; } = [];
    public IReadOnlyList<Workspace.RepositoryDeclaration> Repositories { get; init; } = [];

    /// <summary>Trigger kinds this workflow is designed to be started by (see <see cref="TriggerDeclaration"/>).</summary>
    public IReadOnlyList<TriggerDeclaration> Triggers { get; init; } = [];

    /// <summary>
    /// Artifact types this workflow can process as its input — the chaining criteria: another
    /// workflow's output of a listed type may be wired to dispatch this workflow. Empty means
    /// unconstrained (any chaining allowed).
    /// </summary>
    public IReadOnlyList<string> ConsumedArtifacts { get; init; } = [];

    /// <summary>
    /// Container port of the workflow's interactive web terminal (ttyd), when it hosts one.
    /// The launcher publishes it to an ephemeral host port and the dashboard proxies it —
    /// authenticated — to the run's owner. Null = no terminal.
    /// </summary>
    public int? InteractiveTerminalPort { get; init; }
}

/// <summary>
/// A trigger kind a workflow declares it is driven by. The trigger itself lives OUTSIDE the
/// workflow (wired per configuration); the declaration tells configurators what to wire.
/// Kinds: <see cref="Mailbox"/>, <see cref="Schedule"/>, <see cref="Artifact"/>.
/// </summary>
public sealed record TriggerDeclaration(string Kind, string? Description = null)
{
    public const string Mailbox = "mailbox";
    public const string Schedule = "schedule";
    public const string Artifact = "artifact";
}

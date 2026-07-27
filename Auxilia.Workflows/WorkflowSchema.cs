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
    /// The run inputs this workflow reads from its dispatch context (see
    /// <see cref="WorkflowInputDescriptor"/>). Declaring none means runs start without input.
    /// </summary>
    public IReadOnlyList<WorkflowInputDescriptor> Inputs { get; init; } = [];

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
/// A workflow can only be triggered in ways it declares here.
/// Kinds: <see cref="Mailbox"/>, <see cref="Schedule"/>, <see cref="Artifact"/>, <see cref="Manual"/>.
/// </summary>
public sealed record TriggerDeclaration(string Kind, string? Description = null)
{
    public const string Mailbox = "mailbox";
    public const string Schedule = "schedule";
    public const string Artifact = "artifact";

    /// <summary>Started by a person from the dashboard with an instruction; needs no wiring.</summary>
    public const string Manual = "manual";
}

/// <summary>
/// One run input a workflow declares: free text that reaches the run as the dispatch-context
/// entry named <paramref name="Name"/> (an input named "instruction" additionally lands as
/// the mail-shaped Body). Dispatch UIs render these generically; a run of a workflow that
/// declares no required input starts without any.
/// </summary>
public sealed record WorkflowInputDescriptor(
    string Name,
    string Label,
    bool Required = false,
    string? Description = null)
{
    /// <summary>
    /// Rendering kind ("Text", "Multiline", "Boolean", "Choice", "Number") — editors render inputs
    /// BY KIND and fall back to plain text on kinds they do not know.
    /// </summary>
    public string Kind { get; init; } = "Text";

    /// <summary>Pre-filled value editors start from.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>The selectable values of a "Choice" input.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>Optional human labels per choice value — editors show the label, store the value.</summary>
    public IReadOnlyDictionary<string, string>? ChoiceLabels { get; init; }
}

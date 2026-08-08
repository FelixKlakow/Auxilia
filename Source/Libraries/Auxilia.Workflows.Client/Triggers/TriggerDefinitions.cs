namespace Auxilia.Workflows.Client.Triggers;

/// <summary>
/// An interval trigger: dispatches a stored configuration (or a registered workflow type
/// inline) every <see cref="IntervalSeconds"/> while enabled. The Core is the authorization
/// authority — the dispatch runs on behalf of <see cref="RunAsPrincipalId"/> and passes the
/// same policy checks as a manual run.
/// </summary>
public sealed record ScheduledTriggerDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required int IntervalSeconds { get; init; }
    /// <summary>Stored Core configuration to run; when null, <see cref="WorkflowType"/> runs inline.</summary>
    public Guid? ConfigurationId { get; init; }
    public string? WorkflowType { get; init; }
    public bool Enabled { get; init; } = true;
    public Guid? RunAsPrincipalId { get; init; }
    public IReadOnlyDictionary<string, string>? Context { get; init; }
    public DateTimeOffset? LastDispatchedUtc { get; init; }
}

/// <summary>
/// An artifact-completion trigger: when an artifact of <see cref="ArtifactType"/> (optionally
/// narrowed to one work item) is persisted, dispatch the follow-up workflow with the artifact
/// reference in its context — chaining workflows without coupling them. Fed by the Core's
/// filtered artifact SSE stream, never by the message bus.
/// </summary>
public sealed record ArtifactTriggerDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string ArtifactType { get; init; }
    /// <summary>Optional narrowing: only artifacts of this work item fire the trigger.</summary>
    public string? WorkItemId { get; init; }
    public Guid? ConfigurationId { get; init; }
    public string? WorkflowType { get; init; }
    public bool Enabled { get; init; } = true;
    public Guid? RunAsPrincipalId { get; init; }
    public IReadOnlyDictionary<string, string>? Context { get; init; }
}

/// <summary>
/// A platform-event trigger: when an event of <see cref="EventType"/> (optionally narrowed to
/// one work item) fires — published by a workflow or by the platform itself (the reserved
/// <c>run.*</c> lifecycle vocabulary) — dispatch the follow-up workflow with the event
/// reference and payload in its context. Fed by the Core's filtered event SSE stream, never
/// by the message bus.
/// </summary>
public sealed record EventTriggerDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string EventType { get; init; }
    /// <summary>Optional narrowing: only events of this work item fire the trigger.</summary>
    public string? WorkItemId { get; init; }
    public Guid? ConfigurationId { get; init; }
    public string? WorkflowType { get; init; }
    public bool Enabled { get; init; } = true;
    public Guid? RunAsPrincipalId { get; init; }
    public IReadOnlyDictionary<string, string>? Context { get; init; }
}

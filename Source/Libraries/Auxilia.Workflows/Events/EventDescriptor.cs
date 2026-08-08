namespace Auxilia.Workflows.Events;

/// <summary>
/// A named event type a workflow declares it publishes. The vocabulary is open — event
/// types are free-form strings per workflow, never a compiled enum.
/// </summary>
public sealed record EventDescriptor(
    string EventType,
    /// <summary>JSON schema of the event payload; null for payload-less events.</summary>
    string? PayloadSchemaJson = null,
    string? Description = null);

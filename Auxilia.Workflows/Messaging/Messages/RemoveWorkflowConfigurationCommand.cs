namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Removes a named workflow configuration on the Core.Runner via the
/// <c>slot-configurations</c> exchange or the per-instance
/// <c>*-slot-seed.remove-configuration</c> queue — the same seeding family as
/// <see cref="UpsertWorkflowConfigurationCommand"/>.
/// </summary>
public sealed record RemoveWorkflowConfigurationCommand(
    /// <summary>Natural key of the configuration; the stored record ID derives from it.</summary>
    string Name);

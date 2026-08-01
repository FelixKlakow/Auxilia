namespace Auxilia.Adapters.Email;

/// <summary>
/// Dispatch seam for the mailbox trigger: turns a matched mail into a workflow run without the
/// adapter knowing <em>how</em> the run reaches the platform. Hosts bind
/// <see cref="CoreClientRunDispatcher"/> (drive the Core Run API).
/// </summary>
public interface ITaskSourceRunDispatcher
{
    /// <summary>
    /// Dispatches one run. With <paramref name="configurationId"/> set, the run comes from a stored
    /// configuration; otherwise <paramref name="workflowType"/> specs it ad hoc — the Core resolves
    /// the registered type's package. Returns a correlation id for the dispatch for auditing.
    /// </summary>
    Task<Guid> DispatchAsync(
        Guid? configurationId, string? workflowType,
        IReadOnlyDictionary<string, string> context, Guid? runAsPrincipalId,
        CancellationToken ct = default);
}

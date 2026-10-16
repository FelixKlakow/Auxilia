namespace Auxilia.Workflows.Actions;

/// <summary>
/// A consequential action a workload is about to take beyond its container, intercepted by an
/// enforcement point the workload cannot bypass (it holds no credential for the action) BEFORE
/// anything happens. <see cref="Kind"/> is an open vocabulary (<see cref="ActionKinds"/> names
/// the platform's own); <see cref="Details"/> is what the action concretely does — the basis of
/// the decision, never the command that started it.
/// </summary>
public sealed record GatedAction(string Kind, string Target, string Summary, IReadOnlyList<string> Details);

public sealed record ActionDecision(bool Allowed, string? Reason = null)
{
    public static readonly ActionDecision Allow = new(true);
    public static ActionDecision Deny(string reason) => new(false, reason);
}

/// <summary>Action kinds raised by the SDK's own enforcement points. Any gateway may add kinds.</summary>
public static class ActionKinds
{
    /// <summary>A git push through the workspace push gateway.</summary>
    public const string GitPush = "git.push";
}

/// <summary>Decides gated actions — registered by whatever governs them right now (an agent session's per-action policies).</summary>
public interface IActionApprover
{
    Task<ActionDecision> DecideAsync(GatedAction action, CancellationToken cancellationToken);
}

/// <summary>
/// The process-wide per-action policy point every enforcement point consults. An agent session
/// registers its approver for its lifetime; with none registered the action is the workflow's
/// own (trusted, signed code — no agent process outlives its session, see
/// <see cref="Sandbox.AgentSandbox.EnterAgentSession"/>) and is allowed. Every decision is
/// observable through <see cref="Decided"/>.
/// </summary>
public static class ActionGate
{
    private static readonly object Gate = new();
    private static readonly List<IActionApprover> Approvers = [];

    /// <summary>Raised for every gated action with its decision (logs, chat views, audit).</summary>
    public static event Action<GatedAction, ActionDecision>? Decided;

    public static IDisposable Register(IActionApprover approver)
    {
        lock (Gate)
            Approvers.Add(approver);
        return new Registration(approver);
    }

    public static async Task<ActionDecision> DecideAsync(GatedAction action, CancellationToken cancellationToken)
    {
        IActionApprover? approver;
        lock (Gate)
            approver = Approvers.Count > 0 ? Approvers[^1] : null;
        var decision = approver is null ? ActionDecision.Allow : await approver.DecideAsync(action, cancellationToken);
        Decided?.Invoke(action, decision);
        return decision;
    }

    private sealed class Registration(IActionApprover approver) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
                Approvers.Remove(approver);
        }
    }
}

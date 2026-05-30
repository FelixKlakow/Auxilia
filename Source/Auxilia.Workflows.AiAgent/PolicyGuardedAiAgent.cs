using Auxilia.Workflows.Policy;

namespace Auxilia.Workflows.AiAgent;

/// <summary>
/// Policy-guarded decorator for <see cref="IAiAgent"/>.
/// Every <c>*Async</c> method checks <see cref="IToolPolicy.IsAllowed"/> before delegating.
/// </summary>
public sealed class PolicyGuardedAiAgent : IAiAgent
{
    private readonly IAiAgent _inner;
    private readonly IToolPolicy _policy;
    private readonly string _slotName;

    public PolicyGuardedAiAgent(IAiAgent inner, IToolPolicy policy, string slotName)
    {
        _inner = inner;
        _policy = policy;
        _slotName = slotName;
    }

    public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed(AiAgentOperation.OpenSession))
            throw new ToolPolicyDeniedException(AiAgentOperation.OpenSession, _slotName);
        return _inner.OpenSessionAsync(options, cancellationToken);
    }
}

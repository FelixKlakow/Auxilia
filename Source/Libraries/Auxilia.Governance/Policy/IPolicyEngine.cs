namespace Auxilia.Governance.Policy;

/// <summary>
/// The single authorization entry point for every enforcement point (gateway, MCP, dispatch,
/// view subscription, artifact resolution). Deny-by-default; every decision is audited.
/// </summary>
public interface IPolicyEngine
{
    Task<PolicyDecision> EvaluateAsync(PolicyContext context, CancellationToken ct = default);
}

namespace Auxilia.Governance.Policy;

public sealed record PolicyDecision(bool Allowed, string Reason)
{
    public static PolicyDecision Allow(string reason) => new(true, reason);
    public static PolicyDecision Deny(string reason) => new(false, reason);
}

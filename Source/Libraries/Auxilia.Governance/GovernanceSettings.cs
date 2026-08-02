namespace Auxilia.Governance;

public sealed class GovernanceSettings
{
    /// <summary>
    /// First-start administrator credentials. Used only when no administrator exists yet;
    /// rotate the password immediately after the first login.
    /// </summary>
    public string? BootstrapAdminUsername { get; set; }

    public string? BootstrapAdminPassword { get; set; }

    /// <summary>
    /// TTL (seconds) of the principal/role cache serving bearer authentication and policy
    /// checks. 0 (default) disables caching — reads hit the store on every request. Keep
    /// small: same-node admin writes invalidate eagerly, other nodes converge within the TTL.
    /// </summary>
    public int PrincipalCacheSeconds { get; set; }
}

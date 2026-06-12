namespace Auxilia.Governance;

public sealed class GovernanceSettings
{
    /// <summary>
    /// First-start administrator credentials. Used only when no administrator exists yet;
    /// rotate the password immediately after the first login.
    /// </summary>
    public string? BootstrapAdminUsername { get; set; }

    public string? BootstrapAdminPassword { get; set; }
}

namespace Auxilia.Workflows;

/// <summary>
/// Names of the environment variables the platform injects into a workflow launch.
/// The instance ID and one-time token authenticate the registration handshake; when they
/// are absent the SDK falls back to a self-generated identity (unauthenticated dev mode).
/// </summary>
public static class WorkflowEnvironmentVariables
{
    public const string RegistrationQueue = "Workflow__RegistrationQueue";
    public const string AnnouncementQueue = "Workflow__AnnouncementQueue";
    public const string SlotActivationQueue = "Workflow__SlotActivationQueue";
    public const string ResourceProxyQueue = "Workflow__ResourceProxyQueue";
    public const string InstanceId = "Workflow__InstanceId";
    public const string InstanceToken = "Workflow__InstanceToken";
    /// <summary>Directory where the workflow writes its declared outputs for persistence.</summary>
    public const string OutputDirectory = "Workflow__OutputDirectory";
    /// <summary>Directory containing the per-run repository workspace (ARCHITECTURE §9).</summary>
    public const string WorkspaceDirectory = "Workflow__WorkspaceDirectory";
    /// <summary>
    /// Prefix of one variable per workspace mount (suffixed with the upper-cased mount id) whose
    /// value is the mount's effective root inside the container, working directory included.
    /// </summary>
    public const string WorkspaceMountPrefix = "Workflow__WorkspaceMount__";

    /// <summary>
    /// Prefix of one variable per workspace mount (suffixed like
    /// <see cref="WorkspaceMountPrefix"/>) carrying the mount's post-binding setup script; the
    /// SDK executes it in the mount's root, inside the container, before the application.
    /// </summary>
    public const string WorkspaceMountSetupPrefix = "Workflow__WorkspaceMountSetup__";

    /// <summary>
    /// The single workspace mount's effective root (per-mount working directory included), or
    /// null when the run carries no mount — or more than one, where no single directory can be
    /// the obvious working root and callers fall back to the workspace root.
    /// </summary>
    public static string? SingleMountRoot()
    {
        string? single = null;
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key
                || !key.StartsWith(WorkspaceMountPrefix, StringComparison.Ordinal)
                || entry.Value is not string { Length: > 0 } root)
                continue;
            if (single is not null && single != root)
                return null;
            single = root;
        }
        return single;
    }
}

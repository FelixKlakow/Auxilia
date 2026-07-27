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
}

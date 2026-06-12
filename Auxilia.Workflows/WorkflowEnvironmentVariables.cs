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
    public const string InstanceId = "Workflow__InstanceId";
    public const string InstanceToken = "Workflow__InstanceToken";
}

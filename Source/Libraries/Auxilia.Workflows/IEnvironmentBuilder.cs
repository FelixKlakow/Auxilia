using Auxilia.Workflows.Environment;

namespace Auxilia.Workflows;

public interface IEnvironmentBuilder
{
    IEnvironmentBuilder RequiresTool(string toolName, string? minVersion = null);
    IEnvironmentBuilder RequiresOs(OsConstraint os);
    IEnvironmentBuilder RequiresPort(int port);
}

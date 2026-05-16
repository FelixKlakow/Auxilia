using Auxilia.Workflows.Environment;

namespace Auxilia.Workflows;

public interface IEnvironmentBuilder
{
    IEnvironmentBuilder RequireTool(Tool tool);
    IEnvironmentBuilder RequireOs(OsConstraint os);
    IEnvironmentBuilder RequirePort(int port);
}

using Auxilia.Workflows.Environment;

namespace Auxilia.Workflows.Internal;

internal sealed class EnvironmentBuilder : IEnvironmentBuilder
{
    private readonly List<IEnvironmentRequirement> _requirements = new();

    internal IReadOnlyList<IEnvironmentRequirement> Requirements => _requirements.AsReadOnly();

    public IEnvironmentBuilder RequireTool(Tool tool)
    {
        _requirements.Add(new ToolRequirement(tool));
        return this;
    }

    public IEnvironmentBuilder RequireOs(OsConstraint os)
    {
        _requirements.Add(new OsRequirement(os));
        return this;
    }

    public IEnvironmentBuilder RequirePort(int port)
    {
        _requirements.Add(new PortRequirement(port));
        return this;
    }
}

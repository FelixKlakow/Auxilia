using Auxilia.Workflows.Environment;

namespace Auxilia.Workflows.Internal;

internal sealed class EnvironmentBuilder : IEnvironmentBuilder
{
    private readonly List<IEnvironmentRequirement> _requirements = new();

    internal IReadOnlyList<IEnvironmentRequirement> Requirements => _requirements.AsReadOnly();

    public IEnvironmentBuilder RequiresTool(string toolName, string? minVersion = null)
    {
        _requirements.Add(new ToolRequirement(toolName, minVersion));
        return this;
    }

    public IEnvironmentBuilder RequiresOs(OsConstraint os)
    {
        _requirements.Add(new OsRequirement(os));
        return this;
    }

    public IEnvironmentBuilder RequiresPort(int port)
    {
        _requirements.Add(new PortRequirement(port));
        return this;
    }
}

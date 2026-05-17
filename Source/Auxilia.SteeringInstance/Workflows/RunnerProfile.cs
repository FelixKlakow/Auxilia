using Auxilia.Workflows.Environment;

namespace Auxilia.SteeringInstance.Workflows;

public sealed class RunnerProfile
{
    public ISet<string> AvailableTools { get; set; } = new HashSet<string>();
    public OsConstraint OperatingSystem { get; set; }
    public ISet<int> OpenPorts { get; set; } = new HashSet<int>();
}

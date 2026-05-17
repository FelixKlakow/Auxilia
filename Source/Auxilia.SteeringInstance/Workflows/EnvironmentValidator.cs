using Auxilia.Workflows;
using Auxilia.Workflows.Environment;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows;

public sealed class EnvironmentValidator(
    IOptions<RunnerProfile> profileOptions,
    ILogger<EnvironmentValidator> logger)
{
    public ValidationResult Validate(WorkflowManifest manifest)
    {
        var profile = profileOptions.Value;
        var unsatisfied = new List<string>();

        foreach (var requirement in manifest.EnvironmentRequirements)
        {
            switch (requirement)
            {
                case ToolRequirement toolReq:
                    if (!profile.AvailableTools.Contains(toolReq.ToolName))
                        unsatisfied.Add($"Tool '{toolReq.ToolName}' is not available on this runner.");
                    break;

                case OsRequirement osReq:
                    if (profile.OperatingSystem != osReq.Os)
                        unsatisfied.Add(
                            $"OS '{osReq.Os}' is required but runner is '{profile.OperatingSystem}'.");
                    break;

                case PortRequirement portReq:
                    if (!profile.OpenPorts.Contains(portReq.Port))
                        unsatisfied.Add($"Port {portReq.Port} is not open on this runner.");
                    break;

                default:
                    logger.LogWarning(
                        "Unknown environment requirement type '{Type}' — treating as unsatisfied.",
                        requirement.GetType().FullName);
                    unsatisfied.Add(
                        $"Unknown requirement type '{requirement.GetType().FullName}'.");
                    break;
            }
        }

        return new ValidationResult(unsatisfied.Count == 0, unsatisfied);
    }
}

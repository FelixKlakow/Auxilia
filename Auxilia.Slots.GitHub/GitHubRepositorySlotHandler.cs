using Auxilia.Workflows;
using Auxilia.Workflows.SourceControl;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.GitHub;

/// <summary>
/// Slot handler for provider type "github-repository": backs a repository slot with
/// read-only source-control access over the GitHub REST API. When the dispatcher prepared a
/// per-run clone of the configured repository (RepositoryUrl-bearing bindings land at
/// /workspace/repos/&lt;slot&gt;), that mount is exposed as the local working path.
/// </summary>
public sealed class GitHubRepositorySlotHandler : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
    {
        switch (slotName)
        {
            case "repository":
                var options = GitHubRepositoryOptions.FromSettings(configuration.Settings);
                var mounted = Path.Combine(
                    Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.WorkspaceDirectory) ?? "/workspace",
                    "repos", slotName);
                if (Directory.Exists(mounted))
                    options = options with { WorkingPath = mounted };
                services.AddScoped<ISourceControlAccess>(_ => new GitHubRepositoryAccess(options));
                break;

            default:
                throw new InvalidOperationException($"Unknown slot: {slotName}");
        }
    }
}

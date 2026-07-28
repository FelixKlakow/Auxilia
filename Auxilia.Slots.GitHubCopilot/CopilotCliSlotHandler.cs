using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.GitHubCopilot;

/// <summary>
/// Slot handler for provider type "github-copilot-cli": backs the "coding-agent" slot with
/// the GitHub Copilot CLI. The token arrives as an encrypted JIT slot setting and is handed
/// to the CLI only through its process environment.
/// </summary>
public sealed class CopilotCliSlotHandler : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
    {
        switch (slotName)
        {
            case "coding-agent":
                var options = CopilotCliOptions.FromSettings(configuration.Settings);
                if (!options.HasCredential)
                    throw new InvalidOperationException(
                        "The github-copilot-cli provider requires a GitHub token with Copilot access.");
                services.AddScoped<ICodingAgent>(_ => options.UseSdkSession
                    ? new CopilotSdkAgent(options)
                    : new CopilotCliAgent(options));
                // Console mode (the interactive CLI in tmux+ttyd) consumes the raw
                // coordinates; the token rides the session environment as GH_TOKEN.
                services.AddScoped(_ => new CodingAgentCredentials(
                    null, null, options.CliPath, options.Model)
                {
                    EnvironmentOverrides = new Dictionary<string, string>
                    {
                        ["GH_TOKEN"] = options.Token!,
                        ["GITHUB_TOKEN"] = options.Token!,
                    }
                });
                break;

            default:
                throw new InvalidOperationException($"Unknown slot: {slotName}");
        }
    }
}

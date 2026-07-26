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
                services.AddScoped<ICodingAgent>(_ => new CopilotCliAgent(options));
                break;

            default:
                throw new InvalidOperationException($"Unknown slot: {slotName}");
        }
    }
}

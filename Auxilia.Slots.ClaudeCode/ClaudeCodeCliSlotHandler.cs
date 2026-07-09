using Auxilia.ClaudeCode.Workflow;
using Auxilia.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.ClaudeCode;

/// <summary>
/// Slot handler for provider type "claude-code-cli": backs the "coding-agent" slot with the
/// real Claude Code CLI. The credential (Claude account token preferred, API key fallback)
/// arrives as an encrypted JIT slot setting and is handed to the CLI only through its
/// process environment.
/// </summary>
public sealed class ClaudeCodeCliSlotHandler : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
    {
        switch (slotName)
        {
            case "coding-agent":
                var options = ClaudeCodeCliOptions.FromSettings(configuration.Settings);
                if (!options.HasCredential)
                    throw new InvalidOperationException(
                        "The claude-code-cli provider requires a credential: connect a Claude "
                        + "account (OAuthToken) or set the fallback ApiKey.");
                services.AddScoped<ICodingAgent>(_ => new ClaudeCodeCliAgent(options));
                // Workflows that drive the CLI themselves (the interactive coding session)
                // consume the raw credentials instead of the headless agent.
                services.AddScoped(_ => new CodingAgentCredentials(
                    options.OAuthToken, options.ApiKey, options.CliPath, options.Model));
                break;

            default:
                throw new InvalidOperationException($"Unknown slot: {slotName}");
        }
    }
}

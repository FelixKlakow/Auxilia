using Auxilia.ClaudeCode.Workflow;
using Auxilia.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.ClaudeCode;

/// <summary>
/// Slot handler for provider type "claude-code-cli": backs the "coding-agent" slot with the
/// real Claude Code CLI. The API key arrives as an encrypted JIT slot setting and is handed
/// to the CLI only through its process environment.
/// </summary>
public sealed class ClaudeCodeCliSlotHandler : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
    {
        switch (slotName)
        {
            case "coding-agent":
                var options = ClaudeCodeCliOptions.FromSettings(configuration.Settings);
                if (string.IsNullOrWhiteSpace(options.ApiKey))
                    throw new InvalidOperationException(
                        "The claude-code-cli provider requires the ApiKey setting.");
                services.AddScoped<ICodingAgent>(_ => new ClaudeCodeCliAgent(options));
                break;

            default:
                throw new InvalidOperationException($"Unknown slot: {slotName}");
        }
    }
}

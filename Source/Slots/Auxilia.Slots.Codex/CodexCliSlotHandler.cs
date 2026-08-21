using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.Codex;

/// <summary>
/// Slot handler for provider type "codex-cli": backs the "coding-agent" slot with the OpenAI
/// Codex CLI. The API key arrives as an encrypted JIT slot setting and is handed to the CLI
/// only through its process environment.
/// </summary>
public sealed class CodexCliSlotHandler : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
    {
        switch (slotName)
        {
            case "coding-agent":
                var options = Demand(CodexCliOptions.FromSettings(configuration.Settings));
                services.AddScoped<ICodingAgent>(_ => new CodexCliAgent(options));
                // Console mode (the interactive CLI in tmux+ttyd) consumes the raw
                // coordinates; the key rides the session environment as OPENAI_API_KEY.
                services.AddScoped(_ => Credentials(options));
                break;

            // The reviewer of the implementation workflow: a SECOND agent instance, keyed by
            // slot name so it never collides with the author's registrations.
            case "review-agent":
                var reviewerOptions = Demand(CodexCliOptions.FromSettings(configuration.Settings));
                services.AddKeyedScoped<ICodingAgent>(slotName, (_, _) => new CodexCliAgent(reviewerOptions));
                services.AddKeyedScoped(slotName, (_, _) => Credentials(reviewerOptions));
                break;

            default:
                throw new InvalidOperationException($"Unknown slot: {slotName}");
        }
    }

    private static CodexCliOptions Demand(CodexCliOptions options)
        => options.HasCredential
            ? options
            : throw new InvalidOperationException(
                "The codex-cli provider requires an OpenAI API key.");

    private static CodingAgentCredentials Credentials(CodexCliOptions options)
        => new(null, options.ApiKey, options.CliPath, options.Model)
        {
            EnvironmentOverrides = new Dictionary<string, string>
            {
                ["OPENAI_API_KEY"] = options.ApiKey!,
            },
            UnattendedCliArguments = "--dangerously-bypass-approvals-and-sandbox",
            CompactCommand = "/compact",
        };
}

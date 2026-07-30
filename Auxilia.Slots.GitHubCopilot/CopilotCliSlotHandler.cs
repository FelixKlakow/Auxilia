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
                    },
                    UnattendedCliArguments = "--allow-all-tools",
                });
                // Driven-console seam (the implementation author): the CLI has no hook system,
                // so turn completion arrives over the platform's hook wire — see
                // CopilotConsoleBridge for who feeds it.
                services.AddScoped<CopilotConsoleBridge>();
                services.AddScoped<IConsoleSessionEventSource>(provider =>
                    provider.GetRequiredService<CopilotConsoleBridge>());
                services.AddScoped<IConsoleSessionPreparer>(provider =>
                    provider.GetRequiredService<CopilotConsoleBridge>());
                break;

            // The reviewer of the implementation workflow: a SECOND agent instance, keyed by
            // slot name so it never collides with the author's registrations.
            case "review-agent":
                var reviewerOptions = CopilotCliOptions.FromSettings(configuration.Settings);
                if (!reviewerOptions.HasCredential)
                    throw new InvalidOperationException(
                        "The github-copilot-cli provider requires a GitHub token with Copilot access.");
                services.AddKeyedScoped<ICodingAgent>(slotName, (_, _) => reviewerOptions.UseSdkSession
                    ? new CopilotSdkAgent(reviewerOptions)
                    : new CopilotCliAgent(reviewerOptions));
                services.AddKeyedScoped(slotName, (_, _) => new CodingAgentCredentials(
                    null, null, reviewerOptions.CliPath, reviewerOptions.Model)
                {
                    EnvironmentOverrides = new Dictionary<string, string>
                    {
                        ["GH_TOKEN"] = reviewerOptions.Token!,
                        ["GITHUB_TOKEN"] = reviewerOptions.Token!,
                    },
                    UnattendedCliArguments = "--allow-all-tools",
                });
                break;

            default:
                throw new InvalidOperationException($"Unknown slot: {slotName}");
        }
    }
}

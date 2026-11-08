using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.GitHubCopilot;

/// <summary>
/// Slot handler for provider type "github-copilot-cli": backs the "coding-agent" slot with
/// the GitHub Copilot CLI. The token arrives as an encrypted JIT slot setting and is handed
/// to the CLI only through its process environment — or, in an SDK session, through the
/// session's token callback, which re-reads the slot (<see cref="CopilotTokenSource"/>) so a
/// token replaced or refreshed during the run reaches the running session.
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
                services.AddKeyedSingleton(slotName, (provider, _) => TokenSourceFor(provider, slotName, options));
                services.AddScoped(provider => CreateAgent(
                    options, provider.GetRequiredKeyedService<CopilotTokenSource>(slotName)));
                // Console mode (the interactive CLI in tmux+ttyd) consumes the raw
                // coordinates; the token rides the session environment as GH_TOKEN — fixed for
                // the console session's life (an interactive CLI's environment cannot change).
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
                services.AddKeyedSingleton(slotName, (provider, _) => TokenSourceFor(provider, slotName, reviewerOptions));
                services.AddKeyedScoped(slotName, (provider, _) => CreateAgent(
                    reviewerOptions, provider.GetRequiredKeyedService<CopilotTokenSource>(slotName)));
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

    private static ICodingAgent CreateAgent(CopilotCliOptions options, CopilotTokenSource tokens)
        => options.UseSdkSession
            ? new CopilotSdkAgent(options, tokens: tokens)
            : new CopilotCliAgent(options, tokens: tokens);

    /// <summary>
    /// The slot's token source: re-reads the slot through the run's
    /// <see cref="ISlotConfigurationSource"/> when the run has one (just-in-time activation), and
    /// keeps the registered token otherwise (bundled configurations, tests).
    /// </summary>
    internal static CopilotTokenSource TokenSourceFor(IServiceProvider provider, string slotName, CopilotCliOptions options)
        => provider.GetService<ISlotConfigurationSource>() is { } source
            ? new CopilotTokenSource(options, async ct => (await source.FetchAsync(slotName, ct)).Settings,
                provider.GetService<TimeProvider>())
            : new CopilotTokenSource(options);
}

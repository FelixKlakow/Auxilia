using Auxilia.AI.AgenticFramework.Maf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Auxilia.AI.AgenticFramework.DependencyInjection;

/// <summary>
/// Dependency injection extensions for the Auxilia AI abstraction layer.
/// </summary>
public static class AuxiliaAiExtensions
{
    /// <summary>
    /// Registers the Microsoft Agent Framework (MAF) implementation backed by a locally running
    /// Ollama instance.  Configure the Ollama URL and default model via
    /// <c>appsettings.json → AuxiliaAI:AgentFramework</c>.
    /// </summary>
    /// <example>
    /// appsettings.json:
    /// <code>
    /// {
    ///   "AuxiliaAI": {
    ///     "AgentFramework": {
    ///       "OllamaBaseUrl": "http://localhost:11434",
    ///       "DefaultModel":  "deepseek-r1:8b"
    ///     }
    ///   }
    /// }
    /// </code>
    /// </example>
    public static IServiceCollection AddOllamaAgentFramework(
        this IServiceCollection services,
        Action<MafSettings>? configure = null)
    {
        var section = services
            .AddOptions<MafSettings>()
            .BindConfiguration(MafSettings.SectionName);

        if (configure is not null)
            section.Configure(configure);

        // IAgentSessionBuilder uses the immutable-builder pattern – the injected
        // instance acts as a "prototype" and every With* call returns a new copy,
        // so reusing the same injected builder across multiple calls is safe.
        // A fresh OllamaChatClient is created per BuildAsync() call so that
        // WithDefaultModel() can influence the model without affecting other sessions.
        services.AddSingleton<IAgentSessionBuilder>(sp =>
            new MafAgentSessionBuilder(sp.GetRequiredService<IOptions<MafSettings>>()));

        return services;
    }
}

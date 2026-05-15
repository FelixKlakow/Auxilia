namespace Auxilia.AI.AgenticFramework.Maf;

/// <summary>
/// Configuration settings for the Microsoft Agent Framework (MAF) implementation.
/// Bind from <c>appsettings.json</c> under the <see cref="SectionName"/> key.
/// </summary>
public sealed class MafSettings
{
    public const string SectionName = "AuxiliaAI:AgentFramework";

    /// <summary>Base URL of the locally running Ollama instance.</summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Default model name used when no per-request override is specified.</summary>
    public string DefaultModel { get; set; } = "deepseek-r1:8b";
}


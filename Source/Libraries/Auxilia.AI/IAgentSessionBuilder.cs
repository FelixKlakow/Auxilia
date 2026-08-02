namespace Auxilia.AI;

/// <summary>
/// Builder, which creates Sessions using a specific AI SDK. Allows configuration of Agent Sessions before starting
/// </summary>
public interface IAgentSessionBuilder
{
    IAgentSessionBuilder WithMcpServerTools(string url, string mcpName);
    IAgentSessionBuilder WithMcpServerToolBlacklist(string url, string mcpName, List<string> blacklistedTools);
    IAgentSessionBuilder WithMcpServerToolWhitelist(string url, string mcpName, List<string> whitelistedTools);
    IAgentSessionBuilder WithWorkflowId(Guid workflowId);
    IAgentSessionBuilder WithSystemPrompt(string systemPrompt);
    IAgentSessionBuilder WithRootDirectory(string rootDirectory);
    /// <summary>Overrides the default model for all requests in the session (unless overridden per request).</summary>
    IAgentSessionBuilder WithDefaultModel(string modelName);
    Task<IAgentSession> BuildAsync();
}
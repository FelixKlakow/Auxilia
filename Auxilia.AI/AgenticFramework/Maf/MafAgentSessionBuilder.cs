using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Auxilia.AI.AgenticFramework.Maf;

/// <summary>
/// Immutable builder for <see cref="MafAgentSession"/>.
/// Every <c>With*</c> call returns a new instance so the original injected builder
/// always represents a pristine baseline, safe to reuse across multiple calls.
/// </summary>
internal sealed class MafAgentSessionBuilder : IAgentSessionBuilder
{
    private readonly MafSettings _settings;
    private readonly string? _systemPrompt;
    private readonly string? _rootDirectory;
    private readonly Guid? _workflowId;
    private readonly string? _defaultModel;
    private readonly IReadOnlyList<McpServerConfig> _mcpServers;

    public MafAgentSessionBuilder(IOptions<MafSettings> settings)
        : this(settings.Value, null, null, null, null, []) { }

    private MafAgentSessionBuilder(
        MafSettings settings,
        string? systemPrompt,
        string? rootDirectory,
        Guid? workflowId,
        string? defaultModel,
        IReadOnlyList<McpServerConfig> mcpServers)
    {
        _settings = settings;
        _systemPrompt = systemPrompt;
        _rootDirectory = rootDirectory;
        _workflowId = workflowId;
        _defaultModel = defaultModel;
        _mcpServers = mcpServers;
    }

    public IAgentSessionBuilder WithSystemPrompt(string systemPrompt)
        => Clone(systemPrompt: systemPrompt);

    public IAgentSessionBuilder WithRootDirectory(string rootDirectory)
        => Clone(rootDirectory: rootDirectory);

    public IAgentSessionBuilder WithWorkflowId(Guid workflowId)
        => Clone(workflowId: workflowId);

    public IAgentSessionBuilder WithDefaultModel(string modelName)
        => Clone(defaultModel: modelName);

    public IAgentSessionBuilder WithMcpServerTools(string url, string mcpName)
        => Clone(mcpServers: [.._mcpServers, new McpServerConfig(url, mcpName)]);

    public IAgentSessionBuilder WithMcpServerToolBlacklist(string url, string mcpName, List<string> blacklistedTools)
        => Clone(mcpServers: [.._mcpServers, new McpServerConfig(url, mcpName, Blacklist: blacklistedTools)]);

    public IAgentSessionBuilder WithMcpServerToolWhitelist(string url, string mcpName, List<string> whitelistedTools)
        => Clone(mcpServers: [.._mcpServers, new McpServerConfig(url, mcpName, Whitelist: whitelistedTools)]);

    public async Task<IAgentSession> BuildAsync()
    {
        var tools = new List<AITool>();
        var mcpClients = new List<McpClient>();

        foreach (var server in _mcpServers)
        {
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri(server.Url) });

            var mcpClient = await McpClient.CreateAsync(transport);
            mcpClients.Add(mcpClient);

            var serverTools = await mcpClient.ListToolsAsync();
            tools.AddRange(ApplyFilter(serverTools, server));
        }

        // Create a fresh IChatClient per session so WithDefaultModel() takes effect.
        var effectiveModel = _defaultModel ?? _settings.DefaultModel;
        var chatClient = new OllamaChatClient(new Uri(_settings.OllamaBaseUrl), effectiveModel);

        // Use the constructor overload that accepts instructions and tools directly.
        var agent = new ChatClientAgent(
            chatClient,
            instructions: _systemPrompt,
            name: null,
            description: null,
            tools: tools.Count > 0 ? tools : null);

        var agentSession = await agent.CreateSessionAsync();

        return new MafAgentSession(agent, agentSession, chatClient, mcpClients, _workflowId);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private MafAgentSessionBuilder Clone(
        string? systemPrompt = null,
        string? rootDirectory = null,
        Guid? workflowId = null,
        string? defaultModel = null,
        IReadOnlyList<McpServerConfig>? mcpServers = null)
        => new(_settings,
            systemPrompt ?? _systemPrompt,
            rootDirectory ?? _rootDirectory,
            workflowId ?? _workflowId,
            defaultModel ?? _defaultModel,
            mcpServers ?? _mcpServers);

    private static IEnumerable<AITool> ApplyFilter(
        IEnumerable<AITool> tools,
        McpServerConfig config)
    {
        if (config.Whitelist is { Count: > 0 })
            return tools.Where(t => config.Whitelist.Contains(t.Name, StringComparer.OrdinalIgnoreCase));
        if (config.Blacklist is { Count: > 0 })
            return tools.Where(t => !config.Blacklist.Contains(t.Name, StringComparer.OrdinalIgnoreCase));
        return tools;
    }
}


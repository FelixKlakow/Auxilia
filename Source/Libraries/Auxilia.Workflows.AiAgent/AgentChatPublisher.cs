using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.AiAgent;

/// <summary>
/// Publishes <see cref="AgentChatEntry"/> items to the hosting workflow's declared
/// "agent-chat" view; a silent no-op when the workflow declares no such view or runs
/// without a platform connection (no <see cref="IViewPublisher"/>).
/// </summary>
public sealed class AgentChatPublisher
{
    private readonly IViewPublisher? _views;
    private readonly string? _viewName;

    public AgentChatPublisher(IViewPublisher? views, string? viewName)
    {
        _views = views;
        _viewName = viewName;
    }

    /// <summary>True when entries actually reach a declared agent-chat view.</summary>
    public bool IsActive => _views is not null && _viewName is not null;

    /// <summary>
    /// Resolves the optional <see cref="IViewPublisher"/> and the first declared view with
    /// renderer key <see cref="AgentChatEntry.RendererKey"/> from the workflow's container.
    /// </summary>
    public static AgentChatPublisher Create(IServiceProvider services)
    {
        var views = services.GetService<IViewPublisher>();
        var declared = services.GetService<DeclaredViews>();
        var chatView = declared?.Views.FirstOrDefault(v =>
            v.Rendering == ViewRendering.Custom && v.RendererKey == AgentChatEntry.RendererKey);
        return new AgentChatPublisher(views, chatView?.Name);
    }

    public Task PublishAsync(AgentChatEntry entry, CancellationToken ct = default)
        => IsActive ? _views!.PublishAsync(_viewName!, entry, ct) : Task.CompletedTask;

    public Task PublishUserAsync(string content, CancellationToken ct = default)
        => PublishAsync(new AgentChatEntry(AgentChatRole.User, content, DateTimeOffset.UtcNow), ct);

    public Task PublishAssistantAsync(string content, string? label = null, CancellationToken ct = default)
        => PublishAsync(new AgentChatEntry(AgentChatRole.Assistant, content, DateTimeOffset.UtcNow, label), ct);

    public Task PublishSystemAsync(string content, CancellationToken ct = default)
        => PublishAsync(new AgentChatEntry(AgentChatRole.System, content, DateTimeOffset.UtcNow), ct);

    public Task PublishToolAsync(
        string toolName, string toolState, string content = "", string? label = null, CancellationToken ct = default)
        => PublishAsync(new AgentChatEntry(
            AgentChatRole.Tool, content, DateTimeOffset.UtcNow, label, toolName, toolState), ct);
}

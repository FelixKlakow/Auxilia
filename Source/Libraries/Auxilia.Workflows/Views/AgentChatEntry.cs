namespace Auxilia.Workflows.Views;

public enum AgentChatRole { User, Assistant, System, Tool }

/// <summary>
/// One entry of an agent conversation view: the shared item contract between agent-style
/// workflows and the dashboard's "agent-chat" custom renderer.
/// </summary>
public sealed record AgentChatEntry(
    AgentChatRole Role,
    string Content,
    DateTimeOffset TimestampUtc,
    /// <summary>Optional display label overriding the role-derived default.</summary>
    string? Label = null,
    string? ToolName = null,
    /// <summary>Tool lifecycle state ("Running", "Success", "Error"); only for tool entries.</summary>
    string? ToolState = null,
    /// <summary>Correlates a tool's start and result entries so renderers show ONE card.</summary>
    string? ToolUseId = null,
    /// <summary>
    /// The WORKFLOW's tag marking this entry as additional information (e.g. "permissions",
    /// "bookkeeping") — an open vocabulary. Tagged entries default to HIDDEN; clients offer a
    /// generic per-tag show toggle and never hardcode tag semantics. Null = always shown.
    /// </summary>
    string? DetailTag = null)
{
    /// <summary>The dashboard renderer plug-in key this contract pairs with.</summary>
    public const string RendererKey = "agent-chat";
}

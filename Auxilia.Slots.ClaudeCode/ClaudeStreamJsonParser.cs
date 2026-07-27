using System.Text.Json;
using Auxilia.Workflows.Views;

namespace Auxilia.Slots.ClaudeCode;

/// <summary>
/// Maps the Claude Code CLI's stream-json output (one JSON event per line) onto
/// <see cref="AgentChatEntry"/> items and captures the terminal result event. Stateful per
/// session: tool_use ids are remembered so tool_result lines carry their tool's name.
/// Unknown or malformed lines are ignored — the CLI's format may grow new event types.
/// </summary>
public sealed class ClaudeStreamJsonParser
{
    private const int MaxContentLength = 4000;

    private readonly Dictionary<string, string> _toolNamesByUseId = [];

    /// <summary>The terminal "result" event, once seen; null while the session is running.</summary>
    public ClaudeSessionResult? Result { get; private set; }

    public IReadOnlyList<AgentChatEntry> ParseLine(string line, DateTimeOffset timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(line))
            return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeProperty))
                return [];

            return typeProperty.GetString() switch
            {
                // The init event is bookkeeping, not conversation — no session "starts" per
                // turn, and the model is visible in the session settings. No chat entry.
                "system" => [],
                "assistant" => ParseMessageContent(root, timestampUtc, isAssistant: true),
                "user" => ParseMessageContent(root, timestampUtc, isAssistant: false),
                "result" => ParseResult(root, timestampUtc),
                _ => []
            };
        }
    }

    private IReadOnlyList<AgentChatEntry> ParseMessageContent(
        JsonElement root, DateTimeOffset timestampUtc, bool isAssistant)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
            return [];

        var entries = new List<AgentChatEntry>();
        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var itemType))
                continue;

            switch (itemType.GetString())
            {
                case "text" when isAssistant:
                    var text = item.TryGetProperty("text", out var textProperty)
                        ? textProperty.GetString()
                        : null;
                    if (text is { Length: > 0 })
                        entries.Add(new AgentChatEntry(
                            AgentChatRole.Assistant, Truncate(text), timestampUtc));
                    break;

                case "tool_use" when isAssistant:
                    var toolName = item.TryGetProperty("name", out var nameProperty)
                        ? nameProperty.GetString() ?? "tool"
                        : "tool";
                    var useId = item.TryGetProperty("id", out var idProperty) &&
                                idProperty.GetString() is { Length: > 0 } rawId
                        ? rawId
                        : null;
                    if (useId is not null)
                        _toolNamesByUseId[useId] = toolName;
                    var input = item.TryGetProperty("input", out var inputProperty)
                        ? Truncate(inputProperty.GetRawText())
                        : string.Empty;
                    entries.Add(new AgentChatEntry(
                        AgentChatRole.Tool, input, timestampUtc,
                        Label: item.TryGetProperty("input", out var labelInput) ? LabelOf(labelInput) : null,
                        ToolName: toolName, ToolState: "Running", ToolUseId: useId,
                        DetailTag: DetailTagOf(toolName)));
                    break;

                case "tool_result" when !isAssistant:
                    var resultUseId = item.TryGetProperty("tool_use_id", out var useIdProperty) &&
                                      useIdProperty.GetString() is { Length: > 0 } id
                        ? id
                        : null;
                    var resolvedName = resultUseId is not null &&
                                       _toolNamesByUseId.TryGetValue(resultUseId, out var known)
                        ? known
                        : "tool";
                    var isError = item.TryGetProperty("is_error", out var errorProperty) &&
                                  errorProperty.ValueKind == JsonValueKind.True;
                    entries.Add(new AgentChatEntry(
                        AgentChatRole.Tool, Truncate(FlattenContent(item)), timestampUtc,
                        ToolName: resolvedName, ToolState: isError ? "Error" : "Success",
                        ToolUseId: resultUseId, DetailTag: DetailTagOf(resolvedName)));
                    break;
            }
        }
        return entries;
    }

    private IReadOnlyList<AgentChatEntry> ParseResult(JsonElement root, DateTimeOffset timestampUtc)
    {
        var subtype = root.TryGetProperty("subtype", out var subtypeProperty)
            ? subtypeProperty.GetString() ?? "unknown"
            : "unknown";
        var isError = root.TryGetProperty("is_error", out var errorProperty) &&
                      errorProperty.ValueKind == JsonValueKind.True;
        var resultText = root.TryGetProperty("result", out var resultProperty)
            ? resultProperty.GetString()
            : null;
        var numTurns = root.TryGetProperty("num_turns", out var turnsProperty) &&
                       turnsProperty.TryGetInt32(out var turns)
            ? turns
            : 0;
        decimal? cost = root.TryGetProperty("total_cost_usd", out var costProperty) &&
                        costProperty.TryGetDecimal(out var costValue)
            ? costValue
            : null;
        long? duration = root.TryGetProperty("duration_ms", out var durationProperty) &&
                         durationProperty.TryGetInt64(out var durationValue)
            ? durationValue
            : null;

        Result = new ClaudeSessionResult(isError, resultText, numTurns, cost, duration, subtype);

        // No chat entry for the result event: turn boundaries are announced by the session
        // (turn-ended), and totals live in the session report — cost lines in the chat are noise.
        return isError
            ? [new AgentChatEntry(AgentChatRole.System, $"Session failed ({subtype}).", timestampUtc, Label: "Claude Code")]
            : [];
    }

    /// <summary>
    /// Plan/task bookkeeping tools are ADDITIONAL information (the plan view carries them) —
    /// tagged so clients hide them by default behind a generic toggle.
    /// </summary>
    private static string? DetailTagOf(string toolName)
        => toolName is "TaskCreate" or "TaskUpdate" or "TodoWrite" or "ExitPlanMode"
            ? "bookkeeping"
            : null;

    /// <summary>
    /// The one input value a human wants on the tool card (the command, the file, the
    /// pattern …) — the full input JSON stays available as the entry content.
    /// </summary>
    private static string? LabelOf(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return null;

        string[] preferredKeys = ["command", "file_path", "path", "pattern", "url", "query", "description"];
        foreach (var key in preferredKeys)
            if (input.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 } text)
                return text.Length <= 80 ? text : text[..77] + "…";
        return null;
    }

    /// <summary>tool_result content is either a plain string or an array of text blocks.</summary>
    private static string FlattenContent(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content))
            return string.Empty;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = content.EnumerateArray()
            .Where(block => block.TryGetProperty("type", out var t) && t.GetString() == "text")
            .Select(block => block.TryGetProperty("text", out var t) ? t.GetString() : null)
            .Where(text => text is { Length: > 0 });
        return string.Join("\n", parts);
    }

    private static string Truncate(string text)
        => text.Length <= MaxContentLength
            ? text
            : text[..MaxContentLength] + "\n… (truncated)";
}

public sealed record ClaudeSessionResult(
    bool IsError,
    string? ResultText,
    int NumTurns,
    decimal? TotalCostUsd,
    long? DurationMs,
    string Subtype);

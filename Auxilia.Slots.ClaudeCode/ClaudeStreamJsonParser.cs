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
                "system" => ParseSystem(root, timestampUtc),
                "assistant" => ParseMessageContent(root, timestampUtc, isAssistant: true),
                "user" => ParseMessageContent(root, timestampUtc, isAssistant: false),
                "result" => ParseResult(root, timestampUtc),
                _ => []
            };
        }
    }

    private static IReadOnlyList<AgentChatEntry> ParseSystem(JsonElement root, DateTimeOffset timestampUtc)
    {
        if (root.TryGetProperty("subtype", out var subtype) && subtype.GetString() != "init")
            return [];

        var model = root.TryGetProperty("model", out var modelProperty)
            ? modelProperty.GetString()
            : null;
        var text = model is { Length: > 0 }
            ? $"Claude Code session started (model {model})."
            : "Claude Code session started.";
        return [new AgentChatEntry(AgentChatRole.System, text, timestampUtc, Label: "Claude Code")];
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
                    if (item.TryGetProperty("id", out var idProperty) &&
                        idProperty.GetString() is { Length: > 0 } useId)
                        _toolNamesByUseId[useId] = toolName;
                    var input = item.TryGetProperty("input", out var inputProperty)
                        ? Truncate(inputProperty.GetRawText())
                        : string.Empty;
                    entries.Add(new AgentChatEntry(
                        AgentChatRole.Tool, input, timestampUtc,
                        ToolName: toolName, ToolState: "Running"));
                    break;

                case "tool_result" when !isAssistant:
                    var resolvedName = item.TryGetProperty("tool_use_id", out var useIdProperty) &&
                                       useIdProperty.GetString() is { Length: > 0 } id &&
                                       _toolNamesByUseId.TryGetValue(id, out var known)
                        ? known
                        : "tool";
                    var isError = item.TryGetProperty("is_error", out var errorProperty) &&
                                  errorProperty.ValueKind == JsonValueKind.True;
                    entries.Add(new AgentChatEntry(
                        AgentChatRole.Tool, Truncate(FlattenContent(item)), timestampUtc,
                        ToolName: resolvedName, ToolState: isError ? "Error" : "Success"));
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

        var summary = isError
            ? $"Session failed ({subtype})."
            : $"Session finished: {numTurns} turn(s)" +
              (cost is { } c ? $", ${c:0.####}" : "") + ".";
        return [new AgentChatEntry(AgentChatRole.System, summary, timestampUtc, Label: "Claude Code")];
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

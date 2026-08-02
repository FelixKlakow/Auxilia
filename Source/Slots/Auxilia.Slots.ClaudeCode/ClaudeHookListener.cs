using System.Net;
using System.Text.Json;
using Auxilia.Workflows.AiAgent.CodingAgent;

namespace Auxilia.Slots.ClaudeCode;

/// <summary>
/// In-container endpoint for Claude Code HOOK callbacks: interactive sessions have no control
/// channel, but the CLI invokes configured hook commands on events (Notification, PreToolUse,
/// PostToolUse, Stop) — those commands POST their stdin payload here, and each payload becomes
/// a <see cref="ConsoleSessionEvent"/>. Loopback only; responses are always empty 200s so a
/// hook can never accidentally feed a decision back into the CLI.
/// </summary>
public sealed class ClaudeHookListener : IConsoleSessionEventSource
{
    private readonly ClaudeCodeCliAgent.PlanTracker _plan = new();
    private HttpListener? _listener;
    private Func<ConsoleSessionEvent, CancellationToken, Task>? _onEvent;
    private CancellationTokenSource? _lifetime;

    /// <summary>The port hook commands post to; only valid after <see cref="StartAsync"/>.</summary>
    public int Port { get; private set; }

    public Task StartAsync(
        Func<ConsoleSessionEvent, CancellationToken, Task> onEvent, CancellationToken cancellationToken)
    {
        _onEvent = onEvent;
        _lifetime = new CancellationTokenSource();

        // HttpListener cannot bind port 0 — probe a free loopback port and claim it.
        for (var attempt = 0; ; attempt++)
        {
            var candidate = FreeLoopbackPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = candidate;
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                // Raced by another bind — try the next free port.
            }
        }

        _ = Task.Run(() => PumpAsync(_lifetime.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public ValueTask StopAsync()
    {
        _lifetime?.Cancel();
        _listener?.Close();
        return ValueTask.CompletedTask;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true } listener)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                return; // closed — the session ended
            }

            try
            {
                using var reader = new StreamReader(context.Request.InputStream);
                var payload = await reader.ReadToEndAsync(ct);
                if (_onEvent is { } handler)
                    foreach (var evt in Interpret(payload))
                        await handler(evt, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A malformed hook payload must never take the session down.
            }
            finally
            {
                // ALWAYS an empty 200: hook stdout feeds CLI decisions — this must stay silent.
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        }
    }

    /// <summary>
    /// One hook payload → zero or more session events: the direct mapping, a turn-ended
    /// enriched with the transcript's closing assistant text, and — for plan-shaped tool
    /// calls — the folded plan snapshot.
    /// </summary>
    internal IReadOnlyList<ConsoleSessionEvent> Interpret(string payloadJson)
    {
        var events = new List<ConsoleSessionEvent>();
        var main = Parse(payloadJson);
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            // The Stop payload names the session transcript — its last assistant text IS the
            // turn's message (hooks never deliver assistant output directly).
            if (main is { Kind: ConsoleSessionEvent.TurnEnded }
                && root.TryGetProperty("transcript_path", out var transcript)
                && transcript.GetString() is { Length: > 0 } path
                && TranscriptClosingText(path) is { Length: > 0 } text)
                main = main with { Message = text };

            if (root.TryGetProperty("hook_event_name", out var hookName)
                && hookName.GetString() == "PreToolUse"
                && root.TryGetProperty("tool_name", out var toolProperty)
                && toolProperty.GetString() is { Length: > 0 } tool
                && root.TryGetProperty("tool_input", out var input))
            {
                if (_plan.Apply(tool, input) is { Count: > 0 } snapshot)
                    events.Add(new ConsoleSessionEvent(ConsoleSessionEvent.PlanUpdated, "")
                    {
                        DetailJson = JsonSerializer.Serialize(snapshot)
                    });

                // A question is attention RIGHT NOW — the CLI's own Notification hook only
                // fires for questions after its 60s idle threshold.
                if (string.Equals(tool, "AskUserQuestion", StringComparison.OrdinalIgnoreCase))
                    events.Add(new ConsoleSessionEvent(
                        ConsoleSessionEvent.Attention, QuestionMessage(input)));
            }
        }
        catch (JsonException)
        {
            // Parse already tolerated it — nothing extra to derive.
        }

        if (main is not null)
            events.Insert(0, main);
        return events;
    }

    /// <summary>
    /// The LAST assistant text of a session transcript (JSONL) — the turn's closing message.
    /// Null when the file is missing or the shape is unknown; the cue still fires without it.
    /// </summary>
    internal static string? TranscriptClosingText(string transcriptPath)
    {
        try
        {
            if (!File.Exists(transcriptPath))
                return null;
            string? last = null;
            foreach (var line in File.ReadLines(transcriptPath))
            {
                if (!line.Contains("\"assistant\"", StringComparison.Ordinal))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant"
                        || !root.TryGetProperty("message", out var message)
                        || !message.TryGetProperty("content", out var content)
                        || content.ValueKind != JsonValueKind.Array)
                        continue;
                    var text = string.Join("\n", content.EnumerateArray()
                        .Where(block => block.TryGetProperty("type", out var kind)
                                        && kind.GetString() == "text")
                        .Select(block => block.TryGetProperty("text", out var t) ? t.GetString() : null)
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (text.Length > 0)
                        last = text;
                }
                catch (JsonException)
                {
                    // Foreign line shapes are fine — only well-formed assistant lines count.
                }
            }

            return last;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>One hook payload (the CLI's stdin JSON) → a session event; null for unknown shapes.</summary>
    internal static ConsoleSessionEvent? Parse(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var hook = root.TryGetProperty("hook_event_name", out var name) ? name.GetString() : null;
            var tool = root.TryGetProperty("tool_name", out var t) ? t.GetString() : null;
            return hook switch
            {
                "Notification" => new ConsoleSessionEvent(
                    ConsoleSessionEvent.Attention,
                    root.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } message
                        ? message
                        : "Claude is waiting for your input."),
                "PreToolUse" => new ConsoleSessionEvent(
                    ConsoleSessionEvent.ToolStarted, ToolMessage(root, tool, "running"))
                {
                    ToolName = tool
                },
                "PostToolUse" => new ConsoleSessionEvent(
                    ConsoleSessionEvent.ToolFinished, $"{tool ?? "tool"} finished")
                {
                    ToolName = tool
                },
                "Stop" => new ConsoleSessionEvent(ConsoleSessionEvent.TurnEnded, ""),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The first question's text as the attention line; a generic line when unreadable.</summary>
    private static string QuestionMessage(JsonElement input)
    {
        if (input.TryGetProperty("questions", out var questions)
            && questions.ValueKind == JsonValueKind.Array)
            foreach (var question in questions.EnumerateArray())
                if (question.TryGetProperty("question", out var text)
                    && text.GetString() is { Length: > 0 } prompt)
                    return $"Claude asks: {prompt}";
        return "Claude asks you a question.";
    }

    private static string ToolMessage(JsonElement root, string? tool, string verb)
    {
        var detail = root.TryGetProperty("tool_input", out var input)
            ? input.GetRawText()
            : null;
        if (detail is { Length: > 220 })
            detail = detail[..220] + "…";
        return detail is { Length: > 0 } ? $"{tool ?? "tool"} {verb}: {detail}" : $"{tool ?? "tool"} {verb}";
    }

    private static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

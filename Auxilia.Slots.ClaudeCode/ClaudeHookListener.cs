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
                if (Parse(payload) is { } evt && _onEvent is { } handler)
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

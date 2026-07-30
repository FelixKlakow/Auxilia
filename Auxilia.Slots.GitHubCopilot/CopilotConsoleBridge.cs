using System.Net;
using System.Text.Json;
using Auxilia.Workflows.AiAgent.CodingAgent;

namespace Auxilia.Slots.GitHubCopilot;

/// <summary>
/// Driven-console seam of the Copilot provider: the CLI itself has NO hook system, so turn
/// completion arrives over the platform's hook wire instead — a loopback listener accepting the
/// same <c>hook_event_name</c> payloads Claude's hooks POST. Preparation announces the port in
/// <c>~/.auxilia/console-hook-port</c>, where the driven stub (system tests, SIM demos) reads
/// it; a session-log tail adapter can feed the same wire for real consoles later.
/// </summary>
public sealed class CopilotConsoleBridge : IConsoleSessionEventSource, IConsoleSessionPreparer
{
    /// <summary>The port announcement read by whatever feeds the wire, relative to HOME.</summary>
    public const string PortFileRelativePath = ".auxilia/console-hook-port";

    private HttpListener? _listener;
    private Func<ConsoleSessionEvent, CancellationToken, Task>? _onEvent;
    private CancellationTokenSource? _lifetime;

    /// <summary>The CLI's home; overridable for tests.</summary>
    public string HomeDirectory { get; init; } =
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The port payloads are posted to; only valid after <see cref="StartAsync"/>.</summary>
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

    public async Task PrepareAsync(string workspaceDirectory, CancellationToken cancellationToken)
    {
        var portFile = Path.Combine(HomeDirectory, PortFileRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(portFile)!);
        await File.WriteAllTextAsync(
            portFile, Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
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
                if (_onEvent is { } handler && Parse(payload) is { } evt)
                    await handler(evt, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A malformed payload must never take the session down.
            }
            finally
            {
                // ALWAYS an empty 200 — the wire is observe-only, never a decision channel.
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        }
    }

    /// <summary>One hook-wire payload → a session event; null for unknown shapes.</summary>
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
                        : "Copilot is waiting for your input."),
                "PreToolUse" => new ConsoleSessionEvent(
                    ConsoleSessionEvent.ToolStarted, $"{tool ?? "tool"} running")
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

    private static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

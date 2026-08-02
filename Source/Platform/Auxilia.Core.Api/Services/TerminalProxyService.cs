using System.Net.WebSockets;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Forwards one authenticated (ticketed) client request to a run's ttyd terminal — plain HTTP
/// for the page and its assets, a bidirectional pump for the websocket. The workflow container
/// is only ever dialed from here; its address never reaches the client.
/// </summary>
public sealed class TerminalProxyService(IHttpClientFactory httpClientFactory)
{
    public async Task ForwardHttpAsync(
        HttpContext http, string endpoint, string path, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("terminal-proxy");
        using var upstream = await client.GetAsync(
            new Uri($"http://{endpoint}/{path}{PassthroughQuery(http)}"),
            HttpCompletionOption.ResponseHeadersRead, ct);

        http.Response.StatusCode = (int)upstream.StatusCode;
        if (upstream.Content.Headers.ContentType is { } contentType)
            http.Response.ContentType = contentType.ToString();
        await upstream.Content.CopyToAsync(http.Response.Body, ct);
    }

    public async Task ForwardWebSocketAsync(
        HttpContext http, string endpoint, string path, CancellationToken ct)
    {
        using var upstream = new ClientWebSocket();
        // ttyd requires its "tty" subprotocol — mirror whatever the client asked for.
        foreach (var protocol in http.WebSockets.WebSocketRequestedProtocols)
            upstream.Options.AddSubProtocol(protocol);
        await upstream.ConnectAsync(new Uri($"ws://{endpoint}/{path}{PassthroughQuery(http)}"), ct);

        using var downstream = await http.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        var clientToContainer = PumpAsync(downstream, upstream, ct);
        var containerToClient = PumpAsync(upstream, downstream, ct);
        await Task.WhenAny(clientToContainer, containerToClient);
        // Either side ended — close both directions; the losing pump unwinds on the aborted socket.
        await TryCloseAsync(upstream);
        await TryCloseAsync(downstream);
    }

    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        try
        {
            while (from.State == WebSocketState.Open && to.State == WebSocketState.Open)
            {
                var received = await from.ReceiveAsync(buffer, ct);
                if (received.MessageType == WebSocketMessageType.Close)
                    return;
                await to.SendAsync(
                    new ArraySegment<byte>(buffer, 0, received.Count),
                    received.MessageType, received.EndOfMessage, ct);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // A vanished peer (closed tab, ended session) is the normal end of a terminal.
        }
    }

    private static async Task TryCloseAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        try
        {
            using var closeWindow = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "session ended", closeWindow.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // Best-effort close only.
        }
    }

    /// <summary>The client's query string minus the ticket — the container never sees credentials.</summary>
    private static string PassthroughQuery(HttpContext http)
    {
        var pairs = http.Request.Query
            .Where(entry => !string.Equals(entry.Key, "ticket", StringComparison.OrdinalIgnoreCase))
            .SelectMany(entry => entry.Value, (entry, value) =>
                $"{Uri.EscapeDataString(entry.Key)}={Uri.EscapeDataString(value ?? "")}")
            .ToList();
        return pairs.Count == 0 ? "" : "?" + string.Join("&", pairs);
    }
}

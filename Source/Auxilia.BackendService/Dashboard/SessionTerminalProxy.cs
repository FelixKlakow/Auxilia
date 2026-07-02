using System.Net.WebSockets;
using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.BackendService.Dashboard;

/// <summary>
/// Authenticated reverse proxy for a run's published web terminal (ttyd): HTTP assets and
/// the tty WebSocket are forwarded to the container's published host port. Access is the
/// run's requester or anyone allowed to manage workflow configurations; every terminal
/// attach is audited. The terminal endpoint itself is never exposed to the browser.
/// </summary>
public static class SessionTerminalProxy
{
    public static void MapSessionTerminal(WebApplication app)
    {
        app.UseWebSockets();
        app.Map("/sessions/{runId:guid}/terminal/{**rest}", HandleAsync).RequireAuthorization();
        app.Map("/sessions/{runId:guid}/terminal", HandleAsync).RequireAuthorization();
    }

    private static async Task HandleAsync(HttpContext http, Guid runId, string? rest)
    {
        var instances = http.RequestServices.GetRequiredService<IDataAccess<WorkflowInstanceRecord>>();
        var run = await instances.ReadAsync(runId, http.RequestAborted);
        if (run?.TerminalEndpoint is not { Length: > 0 } endpoint)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (await DenyAsync(http, run) is { } denyReason)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsync(denyReason);
            return;
        }

        if (http.WebSockets.IsWebSocketRequest)
        {
            await ProxyWebSocketAsync(http, endpoint, rest ?? "");
            return;
        }

        await ProxyHttpAsync(http, endpoint, rest ?? "");
    }

    /// <summary>Requester of the run, or a principal allowed to manage workflow configurations.</summary>
    private static async Task<string?> DenyAsync(HttpContext http, WorkflowInstanceRecord run)
    {
        var principalId = DashboardAuthEndpoints.PrincipalIdOf(http.User);
        if (principalId is null)
            return "no platform principal in session";

        var isRequester = RequesterOf(run) == principalId;
        if (!isRequester)
        {
            var policy = http.RequestServices.GetRequiredService<IPolicyEngine>();
            var decision = await policy.EvaluateAsync(
                new PolicyContext(principalId.Value, PermissionActions.WorkflowConfigurationManage,
                    $"session-terminal:{run.Id}") { WorkflowType = run.WorkflowType },
                http.RequestAborted);
            if (!decision.Allowed)
                return decision.Reason;
        }

        // Audit the attach, not every asset request: the WebSocket upgrade is the session.
        if (http.WebSockets.IsWebSocketRequest)
        {
            var audit = http.RequestServices.GetRequiredService<AuditLog>();
            await audit.AppendAsync(principalId.Value.ToString("D"), "session-terminal.attached",
                run.Id.ToString(), run.WorkflowType, ct: http.RequestAborted);
        }

        return null;
    }

    private static Guid? RequesterOf(WorkflowInstanceRecord run)
    {
        try
        {
            return System.Text.Json.JsonSerializer
                .Deserialize<Auxilia.Workflows.Messaging.Messages.RunWorkflowCommand>(
                    run.DispatchCommandJson ?? "")?.RequestedBy;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task ProxyHttpAsync(HttpContext http, string endpoint, string rest)
    {
        var client = http.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("session-terminal");
        using var upstream = await client.SendAsync(
            new HttpRequestMessage(new HttpMethod(http.Request.Method),
                $"http://{endpoint}/{rest}{http.Request.QueryString}"),
            HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);

        http.Response.StatusCode = (int)upstream.StatusCode;
        if (upstream.Content.Headers.ContentType is { } contentType)
            http.Response.ContentType = contentType.ToString();
        await upstream.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
    }

    private static async Task ProxyWebSocketAsync(HttpContext http, string endpoint, string rest)
    {
        using var upstream = new ClientWebSocket();
        // ttyd speaks the "tty" subprotocol; mirror whatever the browser offered.
        foreach (var protocol in http.WebSockets.WebSocketRequestedProtocols)
            upstream.Options.AddSubProtocol(protocol);
        await upstream.ConnectAsync(
            new Uri($"ws://{endpoint}/{rest}{http.Request.QueryString}"), http.RequestAborted);

        using var downstream = await http.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        var pumps = Task.WhenAll(
            PumpAsync(downstream, upstream, http.RequestAborted),
            PumpAsync(upstream, downstream, http.RequestAborted));
        try
        {
            await pumps;
        }
        catch (OperationCanceledException)
        {
            // Browser navigated away or the run ended — both are normal detach paths.
        }
        catch (WebSocketException)
        {
            // Container exited mid-session (CLI /exit) — the run completes normally.
        }
    }

    private static async Task PumpAsync(WebSocket source, WebSocket target, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (source.State == WebSocketState.Open && target.State == WebSocketState.Open)
        {
            var received = await source.ReceiveAsync(buffer, ct);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                await target.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure, "peer closed", ct);
                return;
            }

            await target.SendAsync(
                new ArraySegment<byte>(buffer, 0, received.Count),
                received.MessageType, received.EndOfMessage, ct);
        }
    }
}

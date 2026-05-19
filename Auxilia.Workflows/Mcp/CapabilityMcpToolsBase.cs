using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Auxilia.Workflows.Mcp;

/// <summary>
/// Abstract base class for capability MCP tool servers.
/// Handles both HTTP (Kestrel + StreamableHttpServerTransport) and
/// named-pipe (StreamServerTransport) transport setup.
/// </summary>
public abstract class CapabilityMcpToolsBase : ICapabilityMcpTools
{
    private readonly McpServerOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    private volatile bool _isStarted;
    private McpTransportConfig? _currentTransport;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private WebApplication? _app;

    protected CapabilityMcpToolsBase(string slotName, McpServerOptions options, ILoggerFactory? loggerFactory = null)
    {
        SlotName = slotName;
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public string SlotName { get; }
    public bool IsStarted => _isStarted;
    public McpTransportConfig? CurrentTransport => _currentTransport;

    public IReadOnlyList<string> ToolNames => _isStarted
        ? (_options.ToolCollection?.Select(t => t.ProtocolTool.Name).ToList() ?? [])
        : [];

    public async Task StartAsync(McpTransportConfig transport, CancellationToken cancellationToken = default)
    {
        if (_isStarted) return;

        _cts = new CancellationTokenSource();

        switch (transport)
        {
            case HttpMcpTransportConfig http:
                await StartHttpAsync(http, cancellationToken);
                break;
            case NamedPipeMcpTransportConfig pipe:
                await StartNamedPipeAsync(pipe, cancellationToken);
                break;
            default:
                throw new NotSupportedException($"Transport type '{transport.GetType().Name}' is not supported.");
        }

        _isStarted = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isStarted) return;

        _isStarted = false;
        _currentTransport = null;
        _cts?.Cancel();

        if (_serverTask is not null)
        {
            try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }

        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
            _app = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    private async Task StartHttpAsync(HttpMcpTransportConfig http, CancellationToken ct)
    {
        var serverTransport = new StreamableHttpServerTransport(_loggerFactory)
        {
            Stateless = true
        };

        var uri = new Uri(http.EndpointUrl.TrimEnd('/'));
        int port = uri.Port == 0 ? FindFreePort() : uri.Port;
        string bindUrl = $"{uri.Scheme}://{uri.Host}:{port}";

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(bindUrl);
        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            var message = await JsonSerializer.DeserializeAsync<JsonRpcMessage>(
                ctx.Request.Body, McpJsonUtilities.DefaultOptions, ctx.RequestAborted);
            using var buffer = new MemoryStream();
            bool hasResponse = await serverTransport.HandlePostRequestAsync(message!, buffer, ctx.RequestAborted);
            if (!hasResponse)
            {
                ctx.Response.StatusCode = 202;
                return;
            }
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.ContentLength = buffer.Length;
            buffer.Position = 0;
            await buffer.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        });

        app.MapGet("/mcp", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.Append("Cache-Control", "no-cache");
            try
            {
                await serverTransport.HandleGetRequestAsync(ctx.Response.Body, ctx.RequestAborted);
            }
            catch (OperationCanceledException) { }
        });

        await app.StartAsync(ct);

        _currentTransport = new HttpMcpTransportConfig($"{bindUrl}/mcp", http.McpServerName);
        _app = app;

        var server = McpServer.Create(serverTransport, _options, _loggerFactory, null);
        _serverTask = RunMcpServerAsync(server, _cts!.Token);
    }

    private Task StartNamedPipeAsync(NamedPipeMcpTransportConfig pipe, CancellationToken ct)
    {
        _currentTransport = pipe;

        var pipeStream = new NamedPipeServerStream(
            pipe.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var serverCancellation = _cts!.Token;

        _serverTask = Task.Run(async () =>
        {
            try
            {
                await pipeStream.WaitForConnectionAsync(serverCancellation);
                var serverTransport = new StreamServerTransport(pipeStream, pipeStream, pipe.McpServerName, _loggerFactory);
                var server = McpServer.Create(serverTransport, _options, _loggerFactory, null);
                await RunMcpServerAsync(server, serverCancellation);
            }
            catch (OperationCanceledException) { }
            finally
            {
                await pipeStream.DisposeAsync();
            }
        });

        return Task.CompletedTask;
    }

    private static async Task RunMcpServerAsync(McpServer server, CancellationToken ct)
    {
        await using (server)
        {
            try { await server.RunAsync(ct); }
            catch (OperationCanceledException) { }
        }
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

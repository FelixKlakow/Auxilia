using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Auxilia.Workflows.Actions;
using Auxilia.Workflows.Sandbox;
using Microsoft.Extensions.Logging;

namespace Auxilia.Workflows.Workspace;

/// <summary>One ref update of a push: <c>old → new</c> on <see cref="RefName"/> (all-zero ids = create/delete).</summary>
public sealed record GitRefUpdate(string OldId, string NewId, string RefName)
{
    public bool IsDelete => NewId.All(c => c == '0');
    public bool IsCreate => OldId.All(c => c == '0');

    public override string ToString()
        => IsDelete ? $"delete {RefName}"
            : IsCreate ? $"create {RefName} at {NewId[..Math.Min(12, NewId.Length)]}"
            : $"update {RefName} {OldId[..Math.Min(12, OldId.Length)]}→{NewId[..Math.Min(12, NewId.Length)]}";
}

/// <summary>A push intercepted by the gateway, before anything reached the remote.</summary>
public sealed record GitPushRequest(string MountId, string Remote, IReadOnlyList<GitRefUpdate> Updates)
{
    public string Describe()
        => Updates.Count == 0 ? $"a push to {Remote} (ref updates unreadable)"
            : $"{string.Join(", ", Updates)} on {Remote}";

    /// <summary>The push as a gated action of kind <see cref="ActionKinds.GitPush"/>.</summary>
    public GatedAction ToAction()
        => new(ActionKinds.GitPush, Remote, Describe(), Updates.Select(u => u.ToString()).ToList());
}

/// <summary>
/// The workspace's ONLY route to a credentialed remote: a loopback git smart-HTTP gateway that
/// holds the push credential (it never enters the workspace, a <c>.git/config</c>, or an agent
/// process), relays fetches, and puts every push — parsed down to its exact ref updates — in
/// front of the per-action policy point (<see cref="ActionGate"/>, kind
/// <see cref="ActionKinds.GitPush"/>) before a byte reaches the remote. Obfuscating a
/// <c>git push</c> command line, or calling the provider API directly, gets an agent nothing:
/// it has no credential, and the gateway judges what the push DOES, not how it was typed.
/// </summary>
public sealed class GitPushGateway : IAsyncDisposable
{
    private static readonly Regex CommandLine =
        new(@"^(?<old>[0-9a-f]{40,64}) (?<new>[0-9a-f]{40,64}) (?<ref>[^\s\0]+)", RegexOptions.CultureInvariant);

    private static readonly string[] ForwardedRequestHeaders =
        ["Accept", "Git-Protocol", "User-Agent", "Accept-Encoding", "Accept-Language"];

    private static readonly string[] ForwardedResponseHeaders =
        ["Cache-Control", "Expires", "Pragma"];

    private readonly HttpListener _listener = new();
    private readonly HttpClient _upstream;
    private readonly IReadOnlyDictionary<string, Uri> _remotes;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public int Port { get; }

    private GitPushGateway(IReadOnlyDictionary<string, Uri> remotes, int port, ILogger logger, HttpMessageHandler? handler)
    {
        _remotes = remotes;
        _logger = logger;
        Port = port;
        _upstream = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    /// <summary>The local URL standing in for <paramref name="mountId"/>'s remote.</summary>
    public string LocalUrlFor(string mountId) => $"http://127.0.0.1:{Port}/{mountId}";

    /// <summary>Starts a gateway serving <paramref name="remotes"/> (mount id → credentialed https remote).</summary>
    public static GitPushGateway Start(
        IReadOnlyDictionary<string, Uri> remotes, ILogger logger, HttpMessageHandler? upstreamHandler = null)
    {
        var gateway = new GitPushGateway(remotes, FreeLoopbackPort(), logger, upstreamHandler);
        gateway._listener.Start();
        gateway._loop = Task.Run(gateway.AcceptLoopAsync);
        return gateway;
    }

    /// <summary>
    /// Serves every push remote the platform announced (<see cref="WorkflowEnvironmentVariables.PushRemotePrefix"/>)
    /// and repoints each mount's <c>origin</c> at the gateway. Null when the run has none.
    /// </summary>
    public static async Task<GitPushGateway?> StartForAnnouncedRemotesAsync(ILogger logger, CancellationToken ct)
    {
        var remotes = new Dictionary<string, Uri>(StringComparer.Ordinal);
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry is not { Key: string key, Value: string value }
                || !key.StartsWith(WorkflowEnvironmentVariables.PushRemotePrefix, StringComparison.Ordinal)
                || !Uri.TryCreate(value, UriKind.Absolute, out var remote)
                || remote.Scheme is not ("https" or "http"))
                continue;
            var mountId = key[WorkflowEnvironmentVariables.PushRemotePrefix.Length..];
            if (System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.WorkspaceMountPrefix + mountId)
                is not { Length: > 0 } mountRoot)
                continue;
            remotes[mountId] = remote;
            roots[mountId] = mountRoot;
        }
        if (remotes.Count == 0)
            return null;

        var gateway = Start(remotes, logger);
        foreach (var (mountId, mountRoot) in roots)
            await RepointOriginAsync(mountRoot, gateway.LocalUrlFor(mountId), logger, ct);
        return gateway;
    }

    private static async Task RepointOriginAsync(string mountRoot, string localUrl, ILogger logger, CancellationToken ct)
    {
        var startInfo = AgentSandbox.Confine(new ProcessStartInfo("git")
        {
            ArgumentList = { "-C", mountRoot, "remote", "set-url", "origin", localUrl },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Failed to start git.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Could not route the workspace at {mountRoot} through the push gateway: {await stderr}");
        logger.LogInformation("Workspace {MountRoot} reaches its remote through the push gateway.", mountRoot);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stopping.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException ex)
            {
                _logger.LogWarning(ex, "Push gateway accept failed.");
                continue;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var path = context.Request.Url!.AbsolutePath.TrimStart('/');
            var slash = path.IndexOf('/');
            var mountId = slash < 0 ? path : path[..slash];
            if (!_remotes.TryGetValue(mountId, out var remote))
            {
                await RespondAsync(response, HttpStatusCode.NotFound, "unknown repository");
                return;
            }
            var suffix = slash < 0 ? string.Empty : path[slash..];

            Stream? body = null;
            if (context.Request.HasEntityBody)
            {
                body = context.Request.InputStream;
                if (suffix.EndsWith("/git-receive-pack", StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(context.Request.Headers["Content-Encoding"]))
                    {
                        await RespondAsync(response, HttpStatusCode.Forbidden,
                            "push denied: encoded push requests are not accepted by the push gateway");
                        return;
                    }
                    var (updates, prefix) = await ReadCommandsAsync(body, _stopping.Token);
                    var request = new GitPushRequest(mountId, Redact(remote), updates);
                    var decision = await ActionGate.DecideAsync(request.ToAction(), _stopping.Token);
                    _logger.LogInformation("Push {Decision}: {Push}{Reason}",
                        decision.Allowed ? "allowed" : "DENIED", request.Describe(),
                        decision.Reason is null ? "" : $" ({decision.Reason})");
                    if (!decision.Allowed)
                    {
                        await RespondAsync(response, HttpStatusCode.Forbidden,
                            $"push denied by policy: {decision.Reason ?? "not approved"}");
                        return;
                    }
                    body = new PrefixedStream(prefix, body);
                }
            }

            await ForwardAsync(context, remote, suffix, body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Push gateway request failed.");
            try
            {
                await RespondAsync(response, HttpStatusCode.BadGateway, "push gateway error");
            }
            catch (Exception)
            {
                // The client is gone or the response already started.
            }
        }
        finally
        {
            try { response.Close(); } catch (Exception) { /* already closed */ }
        }
    }

    private async Task ForwardAsync(HttpListenerContext context, Uri remote, string suffix, Stream? body)
    {
        var upstreamUri = new UriBuilder(remote)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Path = remote.AbsolutePath.TrimEnd('/') + suffix,
            Query = context.Request.Url!.Query.TrimStart('?')
        }.Uri;
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), upstreamUri);
        foreach (var name in ForwardedRequestHeaders)
            if (context.Request.Headers[name] is { Length: > 0 } value)
                request.Headers.TryAddWithoutValidation(name, value);
        if (!string.IsNullOrEmpty(remote.UserInfo))
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(remote.UserInfo))));
        if (body is not null)
        {
            request.Content = new StreamContent(body);
            if (context.Request.ContentType is { Length: > 0 } contentType)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            if (context.Request.Headers["Content-Encoding"] is { Length: > 0 } encoding)
                request.Content.Headers.TryAddWithoutValidation("Content-Encoding", encoding);
            if (body is not PrefixedStream && context.Request.ContentLength64 >= 0)
                request.Content.Headers.ContentLength = context.Request.ContentLength64;
        }

        using var upstream = await _upstream.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, _stopping.Token);
        var response = context.Response;
        response.StatusCode = (int)upstream.StatusCode;
        if (upstream.Content.Headers.ContentType is { } upstreamType)
            response.ContentType = upstreamType.ToString();
        if (upstream.Content.Headers.ContentEncoding.Count > 0)
            response.AddHeader("Content-Encoding", string.Join(",", upstream.Content.Headers.ContentEncoding));
        foreach (var name in ForwardedResponseHeaders)
            if (upstream.Headers.TryGetValues(name, out var values))
                response.AddHeader(name, string.Join(",", values));
        if (upstream.Content.Headers.ContentLength is { } length)
            response.ContentLength64 = length;
        else
            response.SendChunked = true;
        await using var content = await upstream.Content.ReadAsStreamAsync(_stopping.Token);
        await content.CopyToAsync(response.OutputStream, _stopping.Token);
    }

    /// <summary>
    /// Reads the receive-pack command section (pkt-lines up to the first flush) and returns the
    /// ref updates plus the exact bytes consumed, which are replayed to the remote unchanged.
    /// </summary>
    internal static async Task<(IReadOnlyList<GitRefUpdate> Updates, byte[] Consumed)> ReadCommandsAsync(
        Stream body, CancellationToken ct)
    {
        const int maxCommandBytes = 4 * 1024 * 1024;
        var consumed = new MemoryStream();
        var updates = new List<GitRefUpdate>();
        var header = new byte[4];
        while (consumed.Length < maxCommandBytes)
        {
            if (!await ReadExactlyAsync(body, header, ct))
                break;
            consumed.Write(header);
            if (!int.TryParse(Encoding.ASCII.GetString(header), System.Globalization.NumberStyles.HexNumber, null, out var length))
                throw new InvalidOperationException("malformed receive-pack request");
            if (length == 0)
                break; // flush: the command list ends, the pack follows
            if (length < 4)
                continue;
            var payload = new byte[length - 4];
            if (!await ReadExactlyAsync(body, payload, ct))
                throw new InvalidOperationException("truncated receive-pack request");
            consumed.Write(payload);
            var line = Encoding.UTF8.GetString(payload);
            if (CommandLine.Match(line) is { Success: true } match)
                updates.Add(new GitRefUpdate(match.Groups["old"].Value, match.Groups["new"].Value, match.Groups["ref"].Value));
        }
        return (updates, consumed.ToArray());
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
                return read == 0 ? false : throw new InvalidOperationException("truncated receive-pack request");
            read += n;
        }
        return true;
    }

    private static async Task RespondAsync(HttpListenerResponse response, HttpStatusCode status, string message)
    {
        response.StatusCode = (int)status;
        response.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(message + "\n");
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private static string Redact(Uri remote)
        => new UriBuilder(remote) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }
        if (_loop is not null)
            await _loop.ConfigureAwait(false);
        _upstream.Dispose();
        _stopping.Dispose();
    }

    /// <summary>The already-read command bytes, then the rest of the request body.</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                var n = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }
            return await rest.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

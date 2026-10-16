using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Auxilia.EgressGateway;

/// <summary>
/// The run's only path to the outside: an HTTP proxy (CONNECT tunnels for TLS, absolute-form
/// requests for plain HTTP) that admits a destination only when <see cref="EgressPolicy"/>
/// allows it, resolves names itself (the workload cannot resolve external names on its internal
/// network), and logs every attempt — allowed and denied — with its byte counts.
/// </summary>
public sealed class EgressProxy(EgressPolicy policy, EgressLog log, IPEndPoint listenOn)
{
    private const int MaxHeadBytes = 64 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private TcpListener? _listener;

    public IPEndPoint LocalEndpoint => (IPEndPoint)(_listener?.LocalEndpoint ?? listenOn);

    public void Start()
    {
        _listener = new TcpListener(listenOn);
        _listener.Start();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _listener ??= StartListener();
        using var registration = ct.Register(() => _listener.Stop());
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private TcpListener StartListener()
    {
        Start();
        return _listener!;
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            var (head, rest) = await ReadHeadAsync(stream, ct);
            if (head is null)
                return;
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length != 3)
            {
                await RejectAsync(stream, 400, "Bad Request", "malformed request line");
                return;
            }

            var (method, target) = (parts[0], parts[1]);
            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                var (host, port) = EgressPolicy.SplitHostPort(target);
                await TunnelAsync(stream, "connect", host, port ?? 443, initial: rest,
                    established: "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
                return;
            }

            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme == "http")
            {
                // Plain HTTP: re-issue the request in origin form; the connection stays pinned
                // to the destination that was checked.
                var forwarded = new StringBuilder($"{method} {uri.PathAndQuery} {parts[2]}\r\n");
                foreach (var header in lines.Skip(1).Where(l => l.Length > 0))
                    if (!header.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                        forwarded.Append(header).Append("\r\n");
                forwarded.Append("\r\n");
                var initial = Encoding.ASCII.GetBytes(forwarded.ToString()).Concat(rest).ToArray();
                await TunnelAsync(stream, "http", uri.Host, uri.Port, initial, established: null, ct);
                return;
            }

            await RejectAsync(stream, 400, "Bad Request", "only CONNECT and absolute-form http requests are proxied");
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // The client went away — nothing to report.
        }
    }

    private async Task TunnelAsync(
        NetworkStream client, string kind, string host, int port, byte[] initial, byte[]? established,
        CancellationToken ct)
    {
        if (policy.IsProtected(host, port))
        {
            log.Write("connect", kind, host, port, "deny", "a protected platform endpoint (reachable only through its relay)");
            await RejectAsync(client, 403, "Forbidden", $"egress to {host}:{port} is not allowed for this run");
            return;
        }
        var match = policy.Check(host, port);
        if (!match.Allowed)
        {
            log.Write("connect", kind, host, port, "deny", "not in the run's egress allowlist");
            await RejectAsync(client, 403, "Forbidden", $"egress to {host}:{port} is not allowed for this run");
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException)
        {
            log.Write("connect", kind, host, port, "error", "name does not resolve");
            await RejectAsync(client, 502, "Bad Gateway", $"{host} does not resolve");
            return;
        }
        var allowed = addresses.Where(a => policy.AllowsAddress(host, a, match.Explicit)).ToArray();
        if (allowed.Length > 0 && await policy.IsProtectedAddressAsync(allowed, port, ct))
        {
            log.Write("connect", kind, host, port, "deny", "resolves to a protected platform endpoint");
            await RejectAsync(client, 403, "Forbidden", $"egress to {host}:{port} is not allowed for this run");
            return;
        }
        if (allowed.Length == 0)
        {
            log.Write("connect", kind, host, port, "deny",
                "resolves only to special-purpose or internal addresses (internal destinations need an explicit entry)");
            await RejectAsync(client, 403, "Forbidden", $"egress to {host}:{port} is not allowed for this run");
            return;
        }

        using var upstream = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            await upstream.ConnectAsync(allowed, port, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            log.Write("connect", kind, host, port, "error", "upstream unreachable");
            await RejectAsync(client, 502, "Bad Gateway", $"{host}:{port} is unreachable");
            return;
        }

        log.Write("connect", kind, host, port, "allow");
        var stopwatch = Stopwatch.StartNew();
        var server = upstream.GetStream();
        if (established is not null)
            await client.WriteAsync(established, ct);
        if (initial.Length > 0)
            await server.WriteAsync(initial, ct);
        var (bytesOut, bytesIn) = await Relay.PipeAsync(client, server, ct);
        log.Write("close", kind, host, port, bytesOut: bytesOut + initial.Length, bytesIn: bytesIn,
            durationMs: stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Reads the request head (up to the blank line); returns the head and any bytes read past it.</summary>
    private static async Task<(string? Head, byte[] Remainder)> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var received = new MemoryStream();
        while (received.Length < MaxHeadBytes)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n == 0)
                return (null, []);
            received.Write(buffer, 0, n);
            var data = received.GetBuffer().AsSpan(0, (int)received.Length);
            var end = data.IndexOf("\r\n\r\n"u8);
            if (end >= 0)
                return (Encoding.ASCII.GetString(data[..end]), data[(end + 4)..].ToArray());
        }
        return (null, []);
    }

    private static async Task RejectAsync(NetworkStream stream, int status, string reason, string message)
    {
        var body = Encoding.UTF8.GetBytes(message + "\n");
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
    }
}

/// <summary>
/// A fixed TCP relay to a platform service the workload must reach (the broker, its terminal).
/// With <paramref name="requiredAmqpUser"/> the relay is an AMQP relay that admits only that
/// broker identity: the client's <c>Connection.Start-Ok</c> is inspected before a byte of it
/// reaches the broker, so the run can never log in as anyone but itself — not even with a
/// platform account's (default or leaked) credentials.
/// </summary>
public sealed class TcpForward(
    int listenPort, string targetHost, int targetPort, string kind, EgressLog log, string? requiredAmqpUser = null,
    EgressPolicy? addressRules = null)
{
    private TcpListener? _listener;

    public int Port => ((IPEndPoint)(_listener?.LocalEndpoint ?? new IPEndPoint(IPAddress.Any, listenPort))).Port;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, listenPort);
        _listener.Start();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_listener is null)
            Start();
        using var registration = ct.Register(() => _listener!.Stop());
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                using var _ = client;
                using var upstream = new TcpClient();
                try
                {
                    if (addressRules is null)
                        await upstream.ConnectAsync(targetHost, targetPort, ct);
                    else if (await AdmittedAddressesAsync(addressRules, ct) is { Length: > 0 } admitted)
                        await upstream.ConnectAsync(admitted, targetPort, ct);
                    else
                    {
                        log.Write("forward", kind, targetHost, targetPort, "deny",
                            "resolves only to special-purpose addresses");
                        return;
                    }
                }
                catch (SocketException)
                {
                    log.Write("forward", kind, targetHost, targetPort, "error", "target unreachable");
                    return;
                }
                var stopwatch = Stopwatch.StartNew();
                if (requiredAmqpUser is not null)
                {
                    await RelayAmqpAsync(client.GetStream(), upstream.GetStream(), requiredAmqpUser, ct);
                    return;
                }
                var (bytesOut, bytesIn) = await Relay.PipeAsync(client.GetStream(), upstream.GetStream(), ct);
                log.Write("forward", kind, targetHost, targetPort, "allow",
                    bytesOut: bytesOut, bytesIn: bytesIn, durationMs: stopwatch.ElapsedMilliseconds);
            }, ct);
        }
    }

    /// <summary>
    /// A declared endpoint's addresses as the gateway resolves them, minus every special-purpose
    /// one (loopback, link-local/metadata, multicast): a declared name is an explicit entry, so
    /// internal addresses pass — rebinding the name onto the gateway's own host never does.
    /// </summary>
    private async Task<IPAddress[]> AdmittedAddressesAsync(EgressPolicy rules, CancellationToken ct)
    {
        if (rules.IsProtected(targetHost, targetPort))
            return [];
        var addresses = IPAddress.TryParse(targetHost, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(targetHost, ct);
        var admitted = addresses.Where(a => rules.AllowsAddress(targetHost, a, explicitMatch: true)).ToArray();
        return await rules.IsProtectedAddressAsync(admitted, targetPort, ct) ? [] : admitted;
    }

    /// <summary>
    /// Relays an AMQP 0-9-1 connection after checking its login: protocol header and the server's
    /// Connection.Start pass through; the client's first frame must be a Connection.Start-Ok with
    /// the PLAIN mechanism naming <paramref name="requiredUser"/> — anything else closes the
    /// connection without the frame ever reaching the broker.
    /// </summary>
    private async Task RelayAmqpAsync(NetworkStream client, NetworkStream server, string requiredUser, CancellationToken ct)
    {
        var header = new byte[8];
        if (!await AmqpLogin.ReadExactlyAsync(client, header, ct))
            return;
        await server.WriteAsync(header, ct);
        var down = Relay.CopyAsync(server, client, ct);

        var frame = await AmqpLogin.ReadFrameAsync(client, ct);
        var user = frame is null ? null : AmqpLogin.PlainUser(frame);
        if (user != requiredUser)
        {
            log.Write("forward", kind, targetHost, targetPort, "deny",
                user is null ? "not a PLAIN AMQP login" : $"login as '{user}' — only the run's own broker identity may connect");
            client.Socket.Close();
            server.Socket.Close();
            await down;
            return;
        }
        await server.WriteAsync(frame!, ct);
        var up = Relay.CopyAsync(client, server, ct);
        var stopwatch = Stopwatch.StartNew();
        var bytesOut = await up + frame!.Length + header.Length;
        var bytesIn = await down;
        log.Write("forward", kind, targetHost, targetPort, "allow",
            bytesOut: bytesOut, bytesIn: bytesIn, durationMs: stopwatch.ElapsedMilliseconds);
    }
}

/// <summary>The minimal AMQP 0-9-1 framing needed to read a client's login.</summary>
internal static class AmqpLogin
{
    private const int MaxFrame = 64 * 1024;

    /// <summary>Reads one whole frame (header, payload, frame-end); null when malformed or too large.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var head = new byte[7];
        if (!await ReadExactlyAsync(stream, head, ct))
            return null;
        var size = (head[3] << 24) | (head[4] << 16) | (head[5] << 8) | head[6];
        if (head[0] != 1 || size < 4 || size > MaxFrame)
            return null;
        var frame = new byte[7 + size + 1];
        head.CopyTo(frame, 0);
        if (!await ReadExactlyAsync(stream, frame.AsMemory(7), ct) || frame[^1] != 0xCE)
            return null;
        return frame;
    }

    /// <summary>The user of a Connection.Start-Ok (class 10, method 11) with the PLAIN mechanism; null otherwise.</summary>
    public static string? PlainUser(byte[] frame)
    {
        try
        {
            var p = 7;
            int U16() { var v = (frame[p] << 8) | frame[p + 1]; p += 2; return v; }
            int U32() { var v = (frame[p] << 24) | (frame[p + 1] << 16) | (frame[p + 2] << 8) | frame[p + 3]; p += 4; return v; }
            if (U16() != 10 || U16() != 11)
                return null;
            var tableLength = U32(); // client-properties field table (read first: U32 advances p)
            p += tableLength;
            var mechanismLength = frame[p++];
            var mechanism = System.Text.Encoding.ASCII.GetString(frame, p, mechanismLength);
            p += mechanismLength;
            if (mechanism != "PLAIN")
                return null;
            var responseLength = U32();
            var response = System.Text.Encoding.UTF8.GetString(frame, p, responseLength).Split('\0');
            return response.Length == 3 && response[0].Length == 0 ? response[1] : null;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }
}

internal static class Relay
{
    /// <summary>Copies both directions until either side closes; returns (client→server, server→client) bytes.</summary>
    public static async Task<(long Out, long In)> PipeAsync(Stream client, Stream server, CancellationToken ct)
    {
        // Each direction ends when its source closes and half-closes its sink, so the peer sees
        // EOF and finishes its side — a request is never cut off from its response.
        var up = CopyAsync(client, server, ct);
        var down = CopyAsync(server, client, ct);
        return (await up, await down);
    }

    internal static async Task<long> CopyAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer, ct)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), ct);
                total += n;
            }
            if (to is NetworkStream network)
                network.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Either side closed — the relay is over.
        }
        return total;
    }
}

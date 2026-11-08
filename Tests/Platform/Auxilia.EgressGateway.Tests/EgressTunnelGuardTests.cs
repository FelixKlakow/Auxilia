using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Auxilia.EgressGateway.Tests;

/// <summary>
/// The gateway holds every connection to the destination it admitted: a CONNECT tunnel's TLS
/// server name must name the tunnel's destination (domain fronting is refused before a byte of the
/// handshake leaves the gateway), and a plain request's Host header is the checked target's.
/// ClientHellos are real ones, produced by <see cref="SslStream"/>.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class EgressTunnelGuardTests
{
    // ------------------------------------------------------------------ ClientHello reading

    [Test]
    public async Task ARealClientHello_YieldsItsServerName()
    {
        var hello = await CaptureClientHelloAsync("api.example.com");

        Assert.That(TlsClientHello.TryReadServerName(hello, out var name), Is.EqualTo(TlsClientHello.Outcome.Parsed));
        Assert.That(name, Is.EqualTo("api.example.com"));
    }

    [Test]
    public async Task AClientHelloArrivingByteByByte_IsIncompleteUntilItsLastByte()
    {
        var hello = await CaptureClientHelloAsync("api.example.com");

        for (var length = 0; length < hello.Length; length++)
            Assert.That(TlsClientHello.TryReadServerName(hello.AsSpan(0, length), out _),
                Is.EqualTo(TlsClientHello.Outcome.Incomplete), $"after {length} of {hello.Length} bytes");
        Assert.That(TlsClientHello.TryReadServerName(hello, out _), Is.EqualTo(TlsClientHello.Outcome.Parsed));
    }

    [Test]
    public async Task AClientHelloSplitAcrossRecords_IsReassembled()
    {
        // Fragmenting the handshake across records is legal — and a classic way to slip past a
        // filter that only reads the first record.
        var hello = await CaptureClientHelloAsync("api.example.com");
        var message = hello.AsSpan(5).ToArray();
        var split = Record(message[..7]).Concat(Record(message[7..40])).Concat(Record(message[40..])).ToArray();

        Assert.That(TlsClientHello.TryReadServerName(split, out var name), Is.EqualTo(TlsClientHello.Outcome.Parsed));
        Assert.That(name, Is.EqualTo("api.example.com"));
    }

    [Test]
    public async Task AClientHelloToAnAddressLiteral_NamesNoServer()
    {
        var hello = await CaptureClientHelloAsync("127.0.0.1");

        Assert.That(TlsClientHello.TryReadServerName(hello, out var name), Is.EqualTo(TlsClientHello.Outcome.Parsed));
        Assert.That(name, Is.Null);
    }

    [Test]
    public void BytesThatAreNotATlsHandshake_AreAnOpaqueTunnel()
        => Assert.That(TlsClientHello.TryReadServerName("SSH-2.0-OpenSSH_9.6\r\n"u8, out _),
            Is.EqualTo(TlsClientHello.Outcome.NotTls));

    [Test]
    public async Task AHandshakeWithTwoServerNameExtensions_IsMalformed()
    {
        // Two server names would let the gateway and the destination disagree about where the
        // tunnel goes — the gateway refuses to guess.
        var hello = await CaptureClientHelloAsync("api.example.com");
        var doubled = WithExtraServerNameExtension(hello, "evil.example");

        Assert.That(TlsClientHello.TryReadServerName(doubled, out _), Is.EqualTo(TlsClientHello.Outcome.Malformed));
    }

    [Test]
    public async Task AHandshakeWhoseLengthsDoNotAddUp_IsMalformed()
    {
        var hello = await CaptureClientHelloAsync("api.example.com");
        var broken = hello.ToArray();
        // The extensions block claims more bytes than the message holds.
        var extensionsAt = ExtensionsOffset(broken);
        broken[extensionsAt] = 0xFF;

        Assert.That(TlsClientHello.TryReadServerName(broken, out _), Is.EqualTo(TlsClientHello.Outcome.Malformed));
    }

    [Test]
    public void ARecordThatIsNotAClientHello_IsMalformed()
        => Assert.That(TlsClientHello.TryReadServerName(Record([2, 0, 0, 1, 0]), out _),
            Is.EqualTo(TlsClientHello.Outcome.Malformed));

    // ------------------------------------------------------------------ tunnel start

    [Test]
    public async Task ATunnelWhoseServerNameIsItsDestination_PassesWithItsBytesIntact()
    {
        var hello = await CaptureClientHelloAsync("api.example.com");

        var start = await EgressProxy.ReadTunnelStartAsync(new MemoryStream(hello), [], "API.example.com.", CancellationToken.None);

        Assert.That(start.Refusal, Is.Null);
        Assert.That(start.ServerName, Is.EqualTo("api.example.com"));
        Assert.That(start.Bytes, Is.EqualTo(hello), "the checked ClientHello is forwarded unchanged");
    }

    [Test]
    public async Task ATunnelNamingAnotherServer_IsRefused()
    {
        var hello = await CaptureClientHelloAsync("evil.example");

        var start = await EgressProxy.ReadTunnelStartAsync(new MemoryStream(hello), [], "allowed.example", CancellationToken.None);

        Assert.That(start.Refusal, Does.Contain("evil.example").And.Contain("allowed.example"));
        Assert.That(start.Bytes, Is.Empty, "nothing of a refused handshake is forwarded");
    }

    [Test]
    public async Task AServerNameToAnAddressDestination_IsRefused()
        => Assert.That((await EgressProxy.ReadTunnelStartAsync(
                new MemoryStream(await CaptureClientHelloAsync("evil.example")), [], "203.0.113.7", CancellationToken.None)).Refusal,
            Is.Not.Null, "a name riding a tunnel to an admitted address is still another destination");

    [Test]
    public async Task AClientHelloAlreadySentWithTheConnectRequest_IsCheckedToo()
    {
        var hello = await CaptureClientHelloAsync("evil.example");

        var start = await EgressProxy.ReadTunnelStartAsync(new MemoryStream(), hello, "allowed.example", CancellationToken.None);

        Assert.That(start.Refusal, Is.Not.Null);
    }

    [Test]
    public async Task AStalledHandshake_IsRefusedAfterTheHelloTimeout()
    {
        var original = EgressProxy.HelloTimeout;
        EgressProxy.HelloTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            var hello = await CaptureClientHelloAsync("api.example.com");
            using var stalled = new StalledStream(hello[..10]);

            var start = await EgressProxy.ReadTunnelStartAsync(stalled, [], "api.example.com", CancellationToken.None);

            Assert.That(start.Refusal, Does.Contain("in time"));
        }
        finally
        {
            EgressProxy.HelloTimeout = original;
        }
    }

    // ------------------------------------------------------------------ through the proxy

    [Test]
    public async Task Connect_WithAForeignServerName_IsRefusedAndLogged_AndNothingReachesTheDestination()
    {
        using var destination = new RecordingServer();
        var output = new StringWriter();
        using var gateway = StartGateway($"127.0.0.1:{destination.Port}", output);
        var hello = await CaptureClientHelloAsync("evil.example");

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, gateway.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{destination.Port} HTTP/1.1\r\n\r\n"));
        Assert.That(await ReadLineAsync(stream), Does.Contain("200"));
        await ReadLineAsync(stream);
        await stream.WriteAsync(hello);

        Assert.That(await ReadToEndAsync(stream), Is.Empty, "the gateway closes the tunnel");
        var connect = Events(output).Single(e => e.GetProperty("event").GetString() == "connect");
        Assert.That(connect.GetProperty("decision").GetString(), Is.EqualTo("deny"));
        Assert.That(connect.GetProperty("sni").GetString(), Is.EqualTo("evil.example"));
        Assert.That(destination.ReceivedBytes, Is.Zero, "not a byte of the handshake left the gateway");
    }

    [Test]
    public async Task Connect_ToAnAddress_WithAHandshakeNamingNoServer_IsForwarded()
    {
        using var destination = new RecordingServer();
        var output = new StringWriter();
        using var gateway = StartGateway($"127.0.0.1:{destination.Port}", output);
        var hello = await CaptureClientHelloAsync("127.0.0.1");

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, gateway.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{destination.Port} HTTP/1.1\r\n\r\n"));
        await ReadLineAsync(stream);
        await ReadLineAsync(stream);
        await stream.WriteAsync(hello);

        Assert.That(await destination.WaitForBytesAsync(hello.Length), Is.EqualTo(hello.Length));
        Assert.That(Events(output).Single(e => e.GetProperty("event").GetString() == "connect")
            .GetProperty("decision").GetString(), Is.EqualTo("allow"));
    }

    // ------------------------------------------------------------------ plain HTTP

    [Test]
    public void APlainRequest_CarriesTheTargetsHost_AndNoHopByHopHeaders_AndClosesAfterOneRequest()
    {
        var forwarded = EgressProxy.OriginFormRequest("GET", new Uri("http://allowed.example:8080/p?q=1"), "HTTP/1.1",
        [
            "Host: evil.example",
            "Connection: keep-alive, X-Hop",
            "Keep-Alive: timeout=5",
            "X-Hop: 1",
            "Proxy-Authorization: Basic eA==",
            "Upgrade: websocket",
            "Accept: */*",
            "Transfer-Encoding: chunked",
        ]);

        Assert.That(forwarded, Is.EqualTo(
            "GET /p?q=1 HTTP/1.1\r\n"
            + "Host: allowed.example:8080\r\n"
            + "Accept: */*\r\n"
            + "Transfer-Encoding: chunked\r\n"
            + "Connection: close\r\n\r\n"));
    }

    [Test]
    public async Task APlainRequestWithAForgedHost_ReachesTheDestinationUnderItsOwnName()
    {
        using var origin = new RecordingServer(respond: true);
        var output = new StringWriter();
        using var gateway = StartGateway($"127.0.0.1:{origin.Port}", output);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, gateway.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET http://127.0.0.1:{origin.Port}/x HTTP/1.1\r\nHost: evil.example\r\n\r\n"));
        await ReadToEndAsync(stream);

        var received = Encoding.ASCII.GetString(origin.Received.ToArray());
        Assert.That(received, Does.Contain($"Host: 127.0.0.1:{origin.Port}\r\n").And.Not.Contain("evil.example"));
        Assert.That(received, Does.Contain("Connection: close"));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The first TLS record a real <see cref="SslStream"/> client sends for <paramref name="targetHost"/>.</summary>
    private static async Task<byte[]> CaptureClientHelloAsync(string targetHost)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        var handshake = ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = targetHost });

        var stream = server.GetStream();
        var header = new byte[5];
        await stream.ReadExactlyAsync(header);
        var body = new byte[(header[3] << 8) | header[4]];
        await stream.ReadExactlyAsync(body);
        server.Close();
        try
        {
            await handshake;
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
        {
            // expected: the "server" never answers
        }
        await ssl.DisposeAsync();
        return [.. header, .. body];
    }

    private static byte[] Record(byte[] fragment)
        => [22, 3, 1, (byte)(fragment.Length >> 8), (byte)fragment.Length, .. fragment];

    /// <summary>Offset of the extensions-length field in a single-record ClientHello.</summary>
    private static int ExtensionsOffset(byte[] hello)
    {
        var p = 5 + 4 + 2 + 32;
        p += 1 + hello[p];
        p += 2 + ((hello[p] << 8) | hello[p + 1]);
        p += 1 + hello[p];
        return p;
    }

    /// <summary>The ClientHello with a second server_name extension appended (all lengths fixed up).</summary>
    private static byte[] WithExtraServerNameExtension(byte[] hello, string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        byte[] list = [0, (byte)(nameBytes.Length >> 8), (byte)nameBytes.Length, .. nameBytes];
        byte[] extension = [0, 0, (byte)((list.Length + 2) >> 8), (byte)(list.Length + 2),
            (byte)(list.Length >> 8), (byte)list.Length, .. list];
        var at = ExtensionsOffset(hello);
        var bytes = hello.Concat(extension).ToArray();
        var extensionsLength = ((hello[at] << 8) | hello[at + 1]) + extension.Length;
        bytes[at] = (byte)(extensionsLength >> 8);
        bytes[at + 1] = (byte)extensionsLength;
        var messageLength = ((hello[6] << 16) | (hello[7] << 8) | hello[8]) + extension.Length;
        bytes[6] = (byte)(messageLength >> 16);
        bytes[7] = (byte)(messageLength >> 8);
        bytes[8] = (byte)messageLength;
        var recordLength = ((hello[3] << 8) | hello[4]) + extension.Length;
        bytes[3] = (byte)(recordLength >> 8);
        bytes[4] = (byte)recordLength;
        return bytes;
    }

    private static List<JsonElement> Events(StringWriter output)
    {
        Thread.Sleep(200);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement).ToList();
    }

    private sealed class RunningGateway(EgressProxy proxy, CancellationTokenSource stop) : IDisposable
    {
        public int Port => proxy.LocalEndpoint.Port;
        public void Dispose() => stop.Cancel();
    }

    private static RunningGateway StartGateway(string allow, StringWriter output)
    {
        var proxy = new EgressProxy(EgressPolicy.Parse(allow), new EgressLog(TextWriter.Synchronized(output)),
            new IPEndPoint(IPAddress.Loopback, 0));
        proxy.Start();
        var stop = new CancellationTokenSource();
        _ = proxy.RunAsync(stop.Token);
        return new RunningGateway(proxy, stop);
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await stream.ReadAsync(one, timeout.Token) == 1 && one[0] != '\n')
            bytes.Add(one[0]);
        return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
    }

    private static async Task<string> ReadToEndAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var all = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                all.Write(buffer, 0, n);
        }
        catch (IOException)
        {
            // reset by the gateway — the connection is over either way
        }
        return Encoding.ASCII.GetString(all.ToArray());
    }

    /// <summary>Accepts one connection and records what arrives; optionally answers one HTTP response.</summary>
    private sealed class RecordingServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly object _gate = new();
        public MemoryStream Received { get; } = new();

        public RecordingServer(bool respond = false)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[4096];
                int n;
                try
                {
                    while ((n = await stream.ReadAsync(buffer)) > 0)
                    {
                        lock (_gate)
                            Received.Write(buffer, 0, n);
                        if (respond && Encoding.ASCII.GetString(Received.ToArray()).Contains("\r\n\r\n"))
                        {
                            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
                            return;
                        }
                    }
                }
                catch (IOException)
                {
                    // the gateway closed it
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public long ReceivedBytes
        {
            get
            {
                Thread.Sleep(200);
                lock (_gate)
                    return Received.Length;
            }
        }

        public async Task<long> WaitForBytesAsync(int expected)
        {
            for (var i = 0; i < 50; i++)
            {
                lock (_gate)
                    if (Received.Length >= expected)
                        return Received.Length;
                await Task.Delay(100);
            }
            lock (_gate)
                return Received.Length;
        }

        public void Dispose() => _listener.Stop();
    }

    /// <summary>A client that sends a few bytes and then goes silent (never closes).</summary>
    private sealed class StalledStream(byte[] prefix) : Stream
    {
        private bool _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                prefix.CopyTo(buffer);
                return prefix.Length;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

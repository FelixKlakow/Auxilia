using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Auxilia.EgressGateway.Tests;

/// <summary>
/// The egress gateway: the allowlist as data, the special-purpose address guard, and real
/// proxied connections (CONNECT and plain HTTP) with every attempt logged.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class EgressGatewayTests
{
    [TestCase("api.nuget.org", "api.nuget.org", 443, true)]
    [TestCase("api.nuget.org", "API.NuGet.org.", 80, true)]
    [TestCase("api.nuget.org:443", "api.nuget.org", 80, false)]
    [TestCase("*.github.com", "api.github.com", 443, true)]
    [TestCase("*.github.com", "github.com", 443, false)]
    [TestCase("*.github.com", "evilgithub.com", 443, false)]
    [TestCase("https://registry.npmjs.org/", "registry.npmjs.org", 443, true)]
    [TestCase("http://internal-api.corp.local:8080", "internal-api.corp.local", 8080, true)]
    [TestCase("", "anything.example", 443, false)]
    [TestCase("*", "anything.example", 443, true)]
    public void Allowlist_MatchesHostsPortsAndWildcards(string allow, string host, int port, bool allowed)
        => Assert.That(EgressPolicy.Parse(allow).Allows(host, port), Is.EqualTo(allowed));

    [TestCase("127.0.0.1")]
    [TestCase("169.254.169.254")]
    [TestCase("::1")]
    [TestCase("0.0.0.0")]
    [TestCase("224.0.0.1")]
    [TestCase("::ffff:127.0.0.1")]
    public void SpecialPurposeAddresses_AreNeverReachedThroughAName_EvenInAllowAll(string address)
    {
        var policy = EgressPolicy.Parse("*");
        Assert.That(policy.AllowsAddress("metadata.example", IPAddress.Parse(address)), Is.False,
            "a name resolving to loopback/link-local/metadata must not open a path to it");
    }

    [Test]
    public void SpecialPurposeAddress_IsReachableOnlyAsAnExplicitlyListedLiteral()
    {
        var policy = EgressPolicy.Parse("127.0.0.1");
        Assert.That(policy.AllowsAddress("127.0.0.1", IPAddress.Loopback), Is.True);
        Assert.That(policy.AllowsAddress("localhost", IPAddress.Loopback), Is.False);
    }

    [Test]
    public void PrivateNetworkAddresses_FollowTheAllowlist()
        => Assert.That(EgressPolicy.Parse("db.corp:5432").AllowsAddress("db.corp", IPAddress.Parse("10.1.2.3")), Is.True,
            "internal corporate endpoints are legitimate destinations when listed");

    [Test]
    public void AProtectedEndpoint_IsRefused_WhileOtherPortsOfItsHostStayReachable()
    {
        var policy = EgressPolicy.Parse("host.docker.internal", "host.docker.internal:5672");

        Assert.That(policy.IsProtected("host.docker.internal", 5672), Is.True);
        Assert.That(policy.IsProtected("HOST.docker.internal.", 5672), Is.True, "names are normalized");
        Assert.That(policy.IsProtected("host.docker.internal", 8418), Is.False);
        Assert.That(policy.Allows("host.docker.internal", 8418), Is.True);
    }

    [Test]
    public async Task AProtectedEndpoint_IsRefused_UnderAnyNameThatResolvesToIt()
    {
        var policy = EgressPolicy.Parse("*", "10.20.30.40:5672");

        Assert.That(await policy.IsProtectedAddressAsync([IPAddress.Parse("10.20.30.40")], 5672, CancellationToken.None), Is.True);
        Assert.That(await policy.IsProtectedAddressAsync([IPAddress.Parse("10.20.30.40")], 443, CancellationToken.None), Is.False);
        Assert.That(await policy.IsProtectedAddressAsync([IPAddress.Parse("10.20.30.41")], 5672, CancellationToken.None), Is.False);
    }

    [Test]
    public void Forwards_AreParsed()
    {
        var specs = ForwardSpec.ParseAll("5672=rabbitmq:5672/bus, 7681=workflow:7681/terminal");
        Assert.That(specs.Select(s => s.ToString()), Is.EqualTo(new[] { "5672=rabbitmq:5672/bus", "7681=workflow:7681/terminal" }));
        Assert.Throws<FormatException>(() => ForwardSpec.ParseAll("nonsense"));
    }

    [Test]
    public async Task Connect_ToAnAllowedDestination_IsTunnelled_AndLogged()
    {
        using var echo = new EchoServer();
        var output = new StringWriter();
        using var gateway = await StartAsync($"127.0.0.1:{echo.Port}", output);

        using var client = await ConnectToProxyAsync(gateway.Port);
        var stream = client.GetStream();
        await WriteAsync(stream, $"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        var established = await ReadLineAsync(stream);
        await ReadLineAsync(stream);
        await WriteAsync(stream, "ping\n");
        var echoed = await ReadLineAsync(stream);

        Assert.That(established, Does.Contain("200"));
        Assert.That(echoed, Is.EqualTo("ping"));
        Assert.That(Events(output).Any(e => e is { Event: "connect", Decision: "allow" }), Is.True);
    }

    [Test]
    public async Task Connect_ToAnUnlistedDestination_IsRefused_AndLoggedAsADenial()
    {
        var output = new StringWriter();
        using var gateway = await StartAsync("api.nuget.org", output);

        using var client = await ConnectToProxyAsync(gateway.Port);
        var stream = client.GetStream();
        await WriteAsync(stream, "CONNECT exfil.example:443 HTTP/1.1\r\nHost: exfil.example\r\n\r\n");
        var status = await ReadLineAsync(stream);

        Assert.That(status, Does.Contain("403"));
        var denial = Events(output).Single(e => e.Event == "connect");
        Assert.That((denial.Decision, denial.Host, denial.Port), Is.EqualTo(("deny", "exfil.example", 443)));
    }

    [Test]
    public async Task Connect_ByAName_ThatResolvesToLoopback_IsRefused()
    {
        using var echo = new EchoServer();
        var output = new StringWriter();
        using var gateway = await StartAsync("localhost", output);

        using var client = await ConnectToProxyAsync(gateway.Port);
        var stream = client.GetStream();
        await WriteAsync(stream, $"CONNECT localhost:{echo.Port} HTTP/1.1\r\n\r\n");

        Assert.That(await ReadLineAsync(stream), Does.Contain("403"));
    }

    [Test]
    public async Task PlainHttp_IsForwardedInOriginForm_WithoutProxyHeaders()
    {
        using var origin = new RecordingHttpServer();
        var output = new StringWriter();
        using var gateway = await StartAsync($"127.0.0.1:{origin.Port}", output);

        using var client = await ConnectToProxyAsync(gateway.Port);
        var stream = client.GetStream();
        await WriteAsync(stream,
            $"GET http://127.0.0.1:{origin.Port}/index.json HTTP/1.1\r\nHost: 127.0.0.1\r\nProxy-Authorization: x\r\nConnection: close\r\n\r\n");
        var status = await ReadLineAsync(stream);

        Assert.That(status, Does.Contain("200"));
        var received = await origin.Received.Task;
        Assert.That(received, Does.StartWith("GET /index.json HTTP/1.1"));
        Assert.That(received, Does.Not.Contain("Proxy-Authorization"));
    }

    // ------------------------------------------------------------------ helpers

    private sealed record LogEvent(string Event, string? Decision, string? Host, int? Port);

    private static List<LogEvent> Events(StringWriter output)
    {
        Thread.Sleep(100);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Select(e => new LogEvent(
                e.GetProperty("event").GetString()!,
                e.TryGetProperty("decision", out var d) ? d.GetString() : null,
                e.TryGetProperty("host", out var h) ? h.GetString() : null,
                e.TryGetProperty("port", out var p) ? p.GetInt32() : null))
            .ToList();
    }

    private sealed class RunningGateway(EgressProxy proxy, CancellationTokenSource stop) : IDisposable
    {
        public int Port => proxy.LocalEndpoint.Port;
        public void Dispose() => stop.Cancel();
    }

    private static Task<RunningGateway> StartAsync(string allow, StringWriter output)
    {
        var proxy = new EgressProxy(EgressPolicy.Parse(allow), new EgressLog(TextWriter.Synchronized(output)),
            new IPEndPoint(IPAddress.Loopback, 0));
        proxy.Start();
        var stop = new CancellationTokenSource();
        _ = proxy.RunAsync(stop.Token);
        return Task.FromResult(new RunningGateway(proxy, stop));
    }

    private static async Task<TcpClient> ConnectToProxyAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        client.ReceiveTimeout = 5000;
        return client;
    }

    private static Task WriteAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await stream.ReadAsync(one, timeout.Token) == 1 && one[0] != '\n')
            bytes.Add(one[0]);
        return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
    }

    private sealed class EchoServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public EchoServer()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[1024];
                int n;
                while ((n = await stream.ReadAsync(buffer)) > 0)
                    await stream.WriteAsync(buffer.AsMemory(0, n));
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public void Dispose() => _listener.Stop();
    }

    private sealed class RecordingHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public TaskCompletionSource<string> Received { get; } = new();

        public RecordingHttpServer()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var head = new StringBuilder();
                while (!head.ToString().Contains("\r\n\r\n"))
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n == 0) break;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }
                Received.TrySetResult(head.ToString());
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public void Dispose() => _listener.Stop();
    }
}

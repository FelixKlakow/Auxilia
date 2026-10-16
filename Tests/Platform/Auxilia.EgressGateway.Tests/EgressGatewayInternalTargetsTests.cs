using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Auxilia.EgressGateway.Tests;

/// <summary>
/// The platform's own services and other runs live on private addresses: allow-all means the
/// public internet, internal destinations need an explicit entry; and the broker relay admits
/// only the run's own broker identity, whatever credentials a workload tries.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class EgressGatewayInternalTargetsTests
{
    [TestCase("10.0.0.5")]
    [TestCase("172.18.0.3")]
    [TestCase("192.168.65.254")]
    [TestCase("100.64.1.1")]
    [TestCase("fd00::1")]
    public void AllowAll_NeverReachesAnInternalAddress_ThroughTheWildcard(string address)
    {
        var policy = EgressPolicy.Parse("*");
        var match = policy.Check("rabbitmq", 15672);

        Assert.That(match.Allowed, Is.True, "the wildcard admits the name...");
        Assert.That(policy.AllowsAddress("rabbitmq", IPAddress.Parse(address), match.Explicit), Is.False,
            "...but not an internal address: the broker's management API, the Core, other runs' gateways");
    }

    [Test]
    public void AllowAll_StillReachesPublicAddresses()
    {
        var policy = EgressPolicy.Parse("*");
        var match = policy.Check("example.com", 443);
        Assert.That(policy.AllowsAddress("example.com", IPAddress.Parse("93.184.216.34"), match.Explicit), Is.True);
    }

    [Test]
    public void AnExplicitEntry_ReachesAnInternalEndpoint()
    {
        var policy = EgressPolicy.Parse("*,internal-api.corp.local");
        var match = policy.Check("internal-api.corp.local", 443);
        Assert.That(match.Explicit, Is.True);
        Assert.That(policy.AllowsAddress("internal-api.corp.local", IPAddress.Parse("10.20.30.40"), match.Explicit), Is.True);
    }

    [Test]
    public void ThePlainLogin_IsReadFromStartOk()
        => Assert.That(AmqpLogin.PlainUser(StartOk("auxilia-wf-abc", "pw")), Is.EqualTo("auxilia-wf-abc"));

    [Test]
    public async Task TheAmqpRelay_AdmitsTheRunsOwnIdentity()
        => Assert.That(await LoginThroughRelayAsync(requiredUser: "auxilia-wf-abc", user: "auxilia-wf-abc"), Is.True);

    [TestCase("guest")]
    [TestCase("auxilia-wf-other-run")]
    public async Task TheAmqpRelay_RefusesAnyOtherLogin_BeforeTheBrokerSeesIt(string user)
        => Assert.That(await LoginThroughRelayAsync(requiredUser: "auxilia-wf-abc", user: user), Is.False);

    /// <summary>
    /// A fake broker that sends Connection.Start and reports whether a Start-Ok reached it;
    /// a client that logs in with PLAIN as <paramref name="user"/> through the relay.
    /// </summary>
    private static async Task<bool> LoginThroughRelayAsync(string requiredUser, string user)
    {
        var broker = new TcpListener(IPAddress.Loopback, 0);
        broker.Start();
        var brokerPort = ((IPEndPoint)broker.LocalEndpoint).Port;
        var startOkReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            using var connection = await broker.AcceptTcpClientAsync();
            var stream = connection.GetStream();
            var header = new byte[8];
            await stream.ReadExactlyAsync(header);
            await stream.WriteAsync(Frame([0, 10, 0, 10, 0, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
            var buffer = new byte[1];
            try
            {
                startOkReached.TrySetResult(await stream.ReadAsync(buffer) > 0);
            }
            catch (IOException)
            {
                startOkReached.TrySetResult(false);
            }
        });

        var relay = new TcpForward(0, "127.0.0.1", brokerPort, "amqp", new EgressLog(TextWriter.Null), requiredUser);
        relay.Start();
        using var stop = new CancellationTokenSource();
        _ = relay.RunAsync(stop.Token);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, relay.Port);
            var stream = client.GetStream();
            await stream.WriteAsync("AMQP\0\0\u0009\u0001"u8.ToArray());
            var startFrame = new byte[8 + 18];
            await stream.ReadExactlyAsync(startFrame);
            await stream.WriteAsync(StartOk(user, "secret"));
            return await startOkReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await stop.CancelAsync();
            broker.Stop();
        }
    }

    private static byte[] StartOk(string user, string password)
    {
        var payload = new List<byte> { 0, 10, 0, 11, 0, 0, 0, 0 }; // class 10, method 11, empty client-properties
        payload.Add(5);
        payload.AddRange("PLAIN"u8.ToArray());
        var response = Encoding.UTF8.GetBytes($"\0{user}\0{password}");
        payload.AddRange([(byte)(response.Length >> 24), (byte)(response.Length >> 16), (byte)(response.Length >> 8), (byte)response.Length]);
        payload.AddRange(response);
        payload.Add(5);
        payload.AddRange("en_US"u8.ToArray());
        return Frame(payload.ToArray());
    }

    private static byte[] Frame(byte[] payload)
    {
        var frame = new List<byte> { 1, 0, 0 };
        frame.AddRange([(byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length]);
        frame.AddRange(payload);
        frame.Add(0xCE);
        return frame.ToArray();
    }
}

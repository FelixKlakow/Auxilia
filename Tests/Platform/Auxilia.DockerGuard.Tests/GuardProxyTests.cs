using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.DockerGuard.Tests;

/// <summary>
/// The guard's pipeline over real sockets: a stand-in daemon behind it records what reaches it.
/// Allowed calls arrive unchanged (method, path, query, body) and their responses stream back —
/// except host binds, which arrive as subpath mounts of the root volume the guard provisions;
/// refused calls never reach the daemon and answer like the daemon itself (403 + message);
/// connection upgrades are never forwarded.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class GuardProxyTests
{
    private WebApplication _daemon = null!;
    private WebApplication _guard = null!;
    private HttpClient _client = null!;
    private readonly ConcurrentQueue<string> _reached = new();
    private volatile bool _rootVolumeExists;

    [SetUp]
    public async Task SetUp()
    {
        _reached.Clear();
        _rootVolumeExists = false;
        var daemon = WebApplication.CreateSlimBuilder();
        daemon.WebHost.UseUrls("http://127.0.0.1:0");
        _daemon = daemon.Build();
        _daemon.Run(StandInDaemonAsync);
        await _daemon.StartAsync();

        _guard = GuardHost.Build(["--urls", "http://127.0.0.1:0"], settings =>
        {
            settings.Upstream = Address(_daemon).Replace("http://", "tcp://");
            settings.BindRoots = ["/var/lib/auxilia/workspaces"];
        });
        await _guard.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(Address(_guard)) };
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.Dispose();
        await _guard.DisposeAsync();
        await _daemon.DisposeAsync();
    }

    [Test]
    public async Task AnAllowedCall_ReachesTheDaemonUnchanged()
    {
        const string body = """{"Image":"wf@sha256:abc","Labels":{"auxilia.workflow":"1"},"HostConfig":{"CapDrop":["ALL"],"SecurityOpt":["no-new-privileges:true"]}}""";
        using var response = await _client.PostAsync("/v1.47/containers/create?name=auxilia-session-1",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("""{"Id":"created"}"""));
        Assert.That(_reached, Does.Contain($"POST /v1.47/containers/create?name=auxilia-session-1 {body}"));
    }

    [Test]
    public async Task AHostBind_ReachesTheDaemon_AsASubpathOfTheRootVolume_ProvisionedOnce()
    {
        const string body = """{"Image":"wf@sha256:abc","Labels":{"auxilia.workflow":"1"},"HostConfig":{"CapDrop":["ALL"],"SecurityOpt":["no-new-privileges:true"],"Binds":["/var/lib/auxilia/workspaces/run-1:/workspace"]}}""";
        for (var i = 0; i < 2; i++)
        {
            using var response = await _client.PostAsync("/containers/create",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created), await response.Content.ReadAsStringAsync());
        }

        var creates = _reached.Where(r => r.StartsWith("POST /containers/create", StringComparison.Ordinal)).ToList();
        Assert.That(creates, Has.Count.EqualTo(2));
        Assert.That(creates[0], Does.Not.Contain("\"Binds\""), "no host path reaches the daemon as a bind");
        Assert.That(creates[0], Does.Contain("\"Subpath\":\"run-1\"").And.Contain(DockerUpstream.RootVolumeName("/var/lib/auxilia/workspaces")));
        var rootVolumes = _reached.Where(r => r.StartsWith("POST /volumes/create", StringComparison.Ordinal)).ToList();
        Assert.That(rootVolumes, Has.Count.EqualTo(1), "the root's volume is created once, then reused");
        Assert.That(rootVolumes[0], Does.Contain("\"device\":\"/var/lib/auxilia/workspaces\"").And.Contain("\"o\":\"bind\""));
    }

    [Test]
    public async Task ARefusedCall_NeverReachesTheDaemon_AndAnswersLikeTheDaemon()
    {
        using var response = await _client.PostAsJsonAsync("/v1.47/containers/owned/exec", new { Cmd = new[] { "sh" } });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using var refusal = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(refusal.RootElement.GetProperty("message").GetString(), Does.StartWith("auxilia docker guard:"),
            "Docker clients surface the daemon's message field");
        Assert.That(_reached.Any(r => r.Contains("/exec")), Is.False);
    }

    [Test]
    public async Task AStreamingResponse_FlowsThroughAsItArrives()
    {
        using var response = await _client.GetAsync("/v1.47/containers/owned/logs?stdout=1", HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        Assert.That(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo("line 1"),
            "the first line arrives while the daemon is still writing — nothing buffers the stream");
        Assert.That(await reader.ReadToEndAsync(), Does.Contain("line 3"));
    }

    [Test]
    public async Task AConnectionUpgrade_IsNeverForwarded()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1.47/containers/owned/attach?stream=1");
        request.Headers.Add("Upgrade", "tcp");
        request.Headers.Add("Connection", "Upgrade");

        using var response = await _client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(_reached.Any(r => r.Contains("/attach")), Is.False);
    }

    [Test]
    public async Task OwnershipIsTheDaemonsAnswer_NotTheCallersClaim()
    {
        using var foreign = await _client.GetAsync("/v1.47/containers/foreign/json");
        using var owned = await _client.GetAsync("/v1.47/containers/owned/json");

        Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(owned.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>Answers like a daemon: labels for inspections, a streamed log, Created for creates, a root volume once created.</summary>
    private async Task StandInDaemonAsync(HttpContext http)
    {
        var body = await new StreamReader(http.Request.Body).ReadToEndAsync();
        var path = http.Request.Path.Value!;
        _reached.Enqueue($"{http.Request.Method} {path}{http.Request.QueryString} {body}".TrimEnd());
        if (path.EndsWith("/version", StringComparison.Ordinal))
        {
            await http.Response.WriteAsJsonAsync(new { ApiVersion = "1.56" });
            return;
        }
        if (path.EndsWith("/volumes/create", StringComparison.Ordinal))
        {
            _rootVolumeExists = true;
            http.Response.StatusCode = StatusCodes.Status201Created;
            await http.Response.WriteAsync(body);
            return;
        }
        if (path.Contains("/volumes/auxilia-guard-root-", StringComparison.Ordinal))
        {
            if (!_rootVolumeExists)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await http.Response.WriteAsJsonAsync(new
            {
                Name = DockerUpstream.RootVolumeName("/var/lib/auxilia/workspaces"),
                Driver = "local",
                Options = new Dictionary<string, string> { ["device"] = "/var/lib/auxilia/workspaces", ["o"] = "bind", ["type"] = "none" },
            });
            return;
        }
        if (path.EndsWith("/containers/owned/json", StringComparison.Ordinal))
        {
            await http.Response.WriteAsJsonAsync(new { Config = new { Labels = new Dictionary<string, string> { ["auxilia.workflow"] = "1" } } });
            return;
        }
        if (path.EndsWith("/containers/foreign/json", StringComparison.Ordinal))
        {
            await http.Response.WriteAsJsonAsync(new { Config = new { Labels = new Dictionary<string, string> { ["com.example.app"] = "web" } } });
            return;
        }
        if (path.EndsWith("/containers/owned/logs", StringComparison.Ordinal))
        {
            for (var i = 1; i <= 3; i++)
            {
                await http.Response.WriteAsync($"line {i}\n");
                await http.Response.Body.FlushAsync();
                await Task.Delay(i == 1 ? 1500 : 100);
            }
            return;
        }
        if (path.EndsWith("/containers/create", StringComparison.Ordinal))
        {
            http.Response.StatusCode = StatusCodes.Status201Created;
            await http.Response.WriteAsync("""{"Id":"created"}""");
            return;
        }
        http.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static string Address(WebApplication app)
        => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
}

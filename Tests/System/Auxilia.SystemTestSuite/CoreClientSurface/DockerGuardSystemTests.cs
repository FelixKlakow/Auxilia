using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;

namespace Auxilia.SystemTestSuite.CoreClientSurface;

/// <summary>
/// The Docker API guard in front of a real daemon, driven exactly like the runner drives it
/// (Docker.DotNet). The runner of this environment reaches Docker ONLY through it — every run in
/// the CoreClientSurface fixtures proves the allowed side; this fixture proves the refusals reach
/// the client as daemon errors and leave nothing behind, and that a symlink a compromised runner
/// plants in its run roots cannot point a bind at the host.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class DockerGuardSystemTests
{
    private const string ProbeRepository = "curlimages/curl";
    private const string ProbeTag = "8.10.1";
    private const string ProbeImage = $"{ProbeRepository}:{ProbeTag}";

    private static DockerClient Guarded()
        => new DockerClientConfiguration(new Uri(CoreClientEnvironment.DockerGuardEndpoint)).CreateClient();

    private static Dictionary<string, string> Owned() => new()
    {
        ["auxilia.workflow"] = "1",
        [TestSession.LabelKey] = TestSession.Id,
    };

    [Test]
    [CancelAfter(120_000)]
    public async Task APrivilegedContainer_IsRefused_AndNeverCreated(CancellationToken ct)
    {
        using var docker = Guarded();
        var name = $"auxilia-guard-probe-{Guid.NewGuid():N}"[..32];

        var refusal = Assert.ThrowsAsync<DockerApiException>(() => docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            Image = ProbeImage,
            Labels = Owned(),
            HostConfig = new HostConfig { Privileged = true, CapDrop = ["ALL"], SecurityOpt = ["no-new-privileges:true"] }
        }, ct));

        Assert.That(refusal!.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Forbidden));
        Assert.That(refusal.ResponseBody, Does.Contain("auxilia docker guard"));
        using var daemon = new DockerClientConfiguration().CreateClient();
        var existing = await daemon.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>> { ["name"] = new Dictionary<string, bool> { [name] = true } }
        }, ct);
        Assert.That(existing, Is.Empty, "a refused create never reaches the daemon");
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task AnotherApplicationsContainer_CannotBeInspectedExecutedOrStopped(CancellationToken ct)
    {
        // A container of "another application": no Auxilia ownership label. Started by this test
        // so a guard that failed open could only ever touch this throwaway.
        await using var foreign = new ContainerBuilder("nginx:1.27-alpine")
            .WithNetwork(CoreClientEnvironment.Network)
            .WithLabel("com.example.app", "web")
            .Build();
        await foreign.StartAsync(ct);
        using var docker = Guarded();

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<DockerApiException>(() => docker.Containers.InspectContainerAsync(foreign.Id, ct),
                "inspection would read another application's environment (its secrets)");
            Assert.ThrowsAsync<DockerApiException>(() => docker.Containers.StopContainerAsync(foreign.Id,
                new ContainerStopParameters(), ct));
        });
        // Exec as an attacker would send it — raw, whatever the client library calls it.
        using var http = new HttpClient { BaseAddress = new Uri(CoreClientEnvironment.DockerGuardEndpoint.Replace("tcp://", "http://")) };
        using var exec = await http.PostAsync($"/containers/{foreign.Id}/exec",
            new StringContent("""{"Cmd":["id"],"AttachStdout":true}""", System.Text.Encoding.UTF8, "application/json"), ct);
        Assert.That(exec.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Forbidden));
        Assert.That(foreign.State, Is.EqualTo(DotNet.Testcontainers.Containers.TestcontainersStates.Running));
    }

    [Test]
    [CancelAfter(120_000)]
    public void TheDaemonSocket_CannotBeBoundIntoAContainer(CancellationToken ct)
    {
        using var docker = Guarded();

        var refusal = Assert.ThrowsAsync<DockerApiException>(() => docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = ProbeImage,
            Labels = Owned(),
            HostConfig = new HostConfig
            {
                CapDrop = ["ALL"],
                SecurityOpt = ["no-new-privileges:true"],
                Binds = ["/var/run/docker.sock:/var/run/docker.sock"]
            }
        }, ct));
        Assert.That(refusal!.ResponseBody, Does.Contain("/var/run/docker.sock"), "refused for the socket bind itself");
    }

    [Test]
    [CancelAfter(300_000)]
    public async Task ASymlinkPlantedInTheRunRoots_CannotPointABindAtTheHost(CancellationToken ct)
    {
        // What a compromised runner process can do: its run roots are writable to it.
        var root = CoreClientEnvironment.RunRootsPath;
        var probe = $"{root}/guard-probe-{Guid.NewGuid():N}";
        await CoreClientEnvironment.ExecInRunnerAsync(
            ["sh", "-c", $"mkdir -p {probe}/data && echo marker > {probe}/data/f && ln -s / {probe}/host"], ct);
        using var docker = Guarded();
        await docker.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = ProbeRepository, Tag = ProbeTag },
            null, new Progress<JSONMessage>(), ct);

        // A real run directory binds as before: the guard carries the bind out beneath the root.
        var readable = await RunToExitAsync(docker, $"{probe}/data:/data:ro", ["cat", "/data/f"], ct);
        Assert.That(readable, Does.Contain("marker"));

        // The planted symlink resolves beneath the root or not at all — never to the host's /.
        var escape = Assert.ThrowsAsync<DockerApiException>(
            () => RunToExitAsync(docker, $"{probe}/host:/host:ro", ["ls", "/host"], ct));
        Assert.That(escape!.ResponseBody, Does.Contain("escapes the base directory"));
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task ReadingTheDaemonsState_StillWorks(CancellationToken ct)
    {
        using var docker = Guarded();
        Assert.That((await docker.System.GetSystemInfoAsync(ct)).OSType, Is.Not.Empty);
        Assert.That(await docker.Containers.ListContainersAsync(new ContainersListParameters(), ct), Is.Not.Null);
    }

    /// <summary>Creates, starts and awaits an owned container with one bind; returns its output.</summary>
    private static async Task<string> RunToExitAsync(DockerClient docker, string bind, IList<string> command, CancellationToken ct)
    {
        string? id = null;
        try
        {
            id = (await docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = ProbeImage,
                Entrypoint = command,
                Labels = Owned(),
                HostConfig = new HostConfig { CapDrop = ["ALL"], SecurityOpt = ["no-new-privileges:true"], Binds = [bind] }
            }, ct)).ID;
            await docker.Containers.StartContainerAsync(id, new ContainerStartParameters(), ct);
            await docker.Containers.WaitContainerAsync(id, ct);
            using var logs = await docker.Containers.GetContainerLogsAsync(id,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true }, ct);
            var (stdout, stderr) = await logs.ReadOutputToEndAsync(ct);
            return stdout + stderr;
        }
        finally
        {
            if (id is not null)
                await docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
        }
    }
}

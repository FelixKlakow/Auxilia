using Auxilia.Core.Runner.Workflows;
using Auxilia.Workflows.Crypto;
using System.Text.Json;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// Tests for <see cref="DockerWorkflowLauncher.BuildCreateContainerParameters"/> — the pure
/// parameter-building logic — without requiring a live Docker daemon.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DockerWorkflowLauncherParamTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"auxilia-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        WriteManifest(_tempDir, "my-workflow", "bin/my-workflow");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static void WriteManifest(string dir, string workflowType, string executableRelativePath)
    {
        var manifest = new WorkflowPackageManifest(
            Files: [],
            SignatureBase64: "c2ln",
            PublicKeyBase64: "a2V5",
            ExecutableRelativePath: executableRelativePath);
        var json = JsonSerializer.Serialize(manifest, WorkflowPackageJsonOptions.SerializeOptions);
        File.WriteAllText(Path.Combine(dir, "package-manifest.json"), json);
    }

    private WorkflowLaunchRequest SimpleRequest(
        string? extractedDir = null,
        Dictionary<string, string>? env = null)
        => new(
            extractedDir ?? _tempDir,
            (IReadOnlyDictionary<string, string>)(env ?? new Dictionary<string, string>()));

    // ------------------------------------------------------------------ image

    [Test]
    public void RuntimeImageIsUsedForContainerImage()
    {
        var settings = new DockerWorkflowLauncherSettings { RuntimeImage = "mcr.microsoft.com/dotnet/runtime:8.0" };
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(SimpleRequest(), settings);

        Assert.That(p.Image, Is.EqualTo("mcr.microsoft.com/dotnet/runtime:8.0"));
    }

    // ------------------------------------------------------------------ bind mount

    [Test]
    public void HostConfigBindsContainsExtractedDirectory()
    {
        var settings = new DockerWorkflowLauncherSettings();
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(SimpleRequest(), settings);

        Assert.That(p.HostConfig.Binds, Is.Not.Null);
        Assert.That(p.HostConfig.Binds, Has.Some.Contains(_tempDir));
        Assert.That(p.HostConfig.Binds, Has.Some.EndsWith(":/workflow:ro"));
    }

    [Test]
    public void WhenWorkspaceDirectoryBindSet_BindsContainReadWriteWorkspaceMount()
    {
        var request = SimpleRequest() with { WorkspaceDirectoryBind = "/host/workspaces/run1" };
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(request, new DockerWorkflowLauncherSettings());

        Assert.That(p.HostConfig.Binds, Does.Contain("/host/workspaces/run1:/workspace"),
            "The workspace must be mounted read-write — workflows commit locally; pushes go through slots.");
    }

    [Test]
    public void WhenWorkspaceDirectoryBindNotSet_NoWorkspaceMount()
    {
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(
            SimpleRequest(), new DockerWorkflowLauncherSettings());

        Assert.That(p.HostConfig.Binds.Any(b => b.EndsWith(":/workspace")), Is.False);
    }

    // ------------------------------------------------------------------ cmd

    [Test]
    public void CmdIsSetToManifestExecutablePath()
    {
        var settings = new DockerWorkflowLauncherSettings();
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(SimpleRequest(), settings);

        Assert.That(p.Cmd, Is.Not.Null);
        Assert.That(p.Cmd, Is.EqualTo(new[] { "/workflow/bin/my-workflow" }));
    }

    // ------------------------------------------------------------------ env vars

    [Test]
    public void WhenEnvVarsProvided_AllAppearAsKeyEqualsValueStrings()
    {
        var env = new Dictionary<string, string>
        {
            ["KEY_A"] = "value_a",
            ["KEY_B"] = "value_b"
        };
        var settings = new DockerWorkflowLauncherSettings();
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(SimpleRequest(env: env), settings);

        Assert.That(p.Env, Does.Contain("KEY_A=value_a"));
        Assert.That(p.Env, Does.Contain("KEY_B=value_b"));
    }

    [Test]
    public void WhenEnvVarValueContainsEquals_SerializedAsSingleEntry()
    {
        var env = new Dictionary<string, string> { ["URL"] = "http://host/path?a=1&b=2" };
        var settings = new DockerWorkflowLauncherSettings();
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(SimpleRequest(env: env), settings);

        Assert.That(p.Env, Does.Contain("URL=http://host/path?a=1&b=2"));
        Assert.That(p.Env!.Count(e => e.StartsWith("URL=")), Is.EqualTo(1));
    }

    [Test]
    public void WhenNoEnvVars_EnvListIsEmpty()
    {
        var settings = new DockerWorkflowLauncherSettings();
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(SimpleRequest(), settings);

        Assert.That(p.Env, Is.Empty);
    }

    // ------------------------------------------------------------------ network

    [Test]
    public void WhenNetworkNameConfigured_NetworkingConfigContainsIt()
    {
        var settings = new DockerWorkflowLauncherSettings { NetworkName = "auxilia-net" };
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(SimpleRequest(), settings);

        Assert.That(p.NetworkingConfig, Is.Not.Null);
        Assert.That(p.NetworkingConfig!.EndpointsConfig.ContainsKey("auxilia-net"), Is.True);
    }

    [Test]
    public void WhenNetworkNameNull_NetworkingConfigIsNull()
    {
        var settings = new DockerWorkflowLauncherSettings { NetworkName = null };
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(SimpleRequest(), settings);

        Assert.That(p.NetworkingConfig, Is.Null);
    }

    [Test]
    public void WhenNetworkNameWhitespace_NetworkingConfigIsNull()
    {
        var settings = new DockerWorkflowLauncherSettings { NetworkName = "   " };
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(SimpleRequest(), settings);

        Assert.That(p.NetworkingConfig, Is.Null);
    }

    // ------------------------------------------------------------------ egress network selection

    private static Auxilia.Core.Runner.Workflows.Egress.EgressPlan Plan(Guid id, int? terminalPort = null)
        => new(id, "api.nuget.org", ["5672=rabbitmq:5672/bus"], terminalPort);

    [Test]
    public void WithAnEgressPlan_TheContainerJoinsOnlyItsRunNetwork_AsTheWorkflowAlias()
    {
        var id = Guid.NewGuid();
        var request = SimpleRequest() with { Egress = Plan(id) };

        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(
            request, new DockerWorkflowLauncherSettings { NetworkName = "auxilia-net" });

        Assert.That(p.NetworkingConfig!.EndpointsConfig.Keys, Is.EqualTo(new[] { $"auxilia-run-{id:N}" }),
            "never the operator's network: the gateway is the run's only way out");
        Assert.That(p.NetworkingConfig.EndpointsConfig.Values.Single().Aliases, Is.EqualTo(new[] { "workflow" }));
    }

    [Test]
    public void WithAnEgressPlan_TheTerminalIsNeverPublishedFromTheWorkflowContainer()
    {
        var request = SimpleRequest() with { Egress = Plan(Guid.NewGuid(), 7681), PublishTerminalPort = 7681 };

        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(request, new DockerWorkflowLauncherSettings());

        Assert.That(p.HostConfig.PortBindings, Is.Null.Or.Empty, "the gateway relays and publishes it");
        Assert.That(p.ExposedPorts!.Keys, Is.EqualTo(new[] { "7681/tcp" }));
    }

    [Test]
    public void TheGateway_IsUnprivileged_AndCarriesThePlanAsData()
    {
        var id = Guid.NewGuid();
        var p = Auxilia.Core.Runner.Workflows.Egress.DockerEgressHost.BuildGatewayParameters(
            Plan(id, 7681), new DockerWorkflowLauncherSettings());

        Assert.Multiple(() =>
        {
            Assert.That(p.Env, Does.Contain("EGRESS_ALLOW=api.nuget.org"));
            Assert.That(p.Env, Does.Contain("EGRESS_FORWARDS=5672=rabbitmq:5672/bus"));
            Assert.That(p.Env, Does.Contain($"EGRESS_AMQP_USER=auxilia-wf-{id:N}"), "the relay admits only the run's own identity");
            Assert.That(p.HostConfig.CapDrop, Is.EqualTo(new[] { "ALL" }));
            Assert.That(p.HostConfig.ReadonlyRootfs, Is.True);
            Assert.That(p.HostConfig.SecurityOpt, Does.Contain("no-new-privileges:true"));
            Assert.That(p.NetworkingConfig.EndpointsConfig.Keys, Is.EqualTo(new[] { $"auxilia-run-{id:N}" }));
            Assert.That(p.NetworkingConfig.EndpointsConfig.Values.Single().Aliases, Is.EqualTo(new[] { "egress" }));
            Assert.That(p.HostConfig.PortBindings!["7681/tcp"].Single().HostIP, Is.EqualTo("127.0.0.1"));
        });
    }

    [Test]
    public void WithoutAnEgressPlan_TheOperatorsNetworkIsUsed()
    {
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(
            SimpleRequest(), new DockerWorkflowLauncherSettings { NetworkName = "auxilia-net" });

        Assert.That(p.NetworkingConfig!.EndpointsConfig.Keys, Is.EqualTo(new[] { "auxilia-net" }));
    }

    // ------------------------------------------------------------------ host config

    [Test]
    public void AutoRemoveIsOff_TheExitWatcherOwnsCleanup()
    {
        // The exit watcher must collect the exit code + log tail BEFORE removal — with
        // AutoRemove a crashed container (and its evidence) would vanish silently.
        var settings = new DockerWorkflowLauncherSettings();
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(SimpleRequest(), settings);

        Assert.That(p.HostConfig.AutoRemove, Is.False);
    }
}

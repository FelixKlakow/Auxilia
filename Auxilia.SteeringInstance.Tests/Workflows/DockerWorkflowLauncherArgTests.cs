using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using System.Text.Json;

namespace Auxilia.SteeringInstance.Tests.Workflows;

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
            WorkflowType: workflowType,
            ExecutableRelativePath: executableRelativePath,
            ContentHashBase64: "aGFzaA==",
            SignatureBase64: "c2ln",
            PublicKeyBase64: "a2V5");
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(Path.Combine(dir, "manifest.json"), json);
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

    // ------------------------------------------------------------------ host config

    [Test]
    public void AutoRemoveIsAlwaysTrue()
    {
        var settings = new DockerWorkflowLauncherSettings();
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(SimpleRequest(), settings);

        Assert.That(p.HostConfig.AutoRemove, Is.True);
    }
}

using Auxilia.SteeringInstance.Workflows;

namespace Auxilia.SteeringInstance.Tests.Workflows;

/// <summary>
/// Tests for <see cref="DockerWorkflowLauncher.BuildCreateContainerParameters"/> — the pure
/// parameter-building logic — without requiring a live Docker daemon.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DockerWorkflowLauncherParamTests
{
    private static WorkflowLaunchRequest SimpleRequest(
        string image = "my-image:latest",
        Dictionary<string, string>? env = null)
        => new(image, (IReadOnlyDictionary<string, string>)(env ?? new Dictionary<string, string>()));

    // ------------------------------------------------------------------ image

    [Test]
    public void ImageIsSetOnParameters()
    {
        var settings = new DockerWorkflowLauncherSettings();
        var p = DockerWorkflowLauncher.BuildCreateContainerParameters(
            SimpleRequest("my-workflow:v2"), settings);

        Assert.That(p.Image, Is.EqualTo("my-workflow:v2"));
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
        // Must be one entry, not split on '='
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

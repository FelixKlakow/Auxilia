using Auxilia.SteeringInstance.Workflows;

namespace Auxilia.SteeringInstance.Tests.Workflows;

/// <summary>
/// Tests for <see cref="DockerWorkflowLauncher.BuildDockerArgs"/> — the pure argument-building
/// logic — without requiring Docker to be present or running.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DockerWorkflowLauncherArgTests
{
    private static WorkflowLaunchRequest SimpleRequest(
        string image = "my-image:latest",
        Dictionary<string, string>? env = null)
        => new(image, (env ?? new Dictionary<string, string>()).AsReadOnly());

    // ------------------------------------------------------------------ network flag

    [Test]
    public void WhenNetworkNameConfigured_NetworkFlagPresentInArgs()
    {
        var settings = new DockerWorkflowLauncherSettings { NetworkName = "auxilia-net" };
        var args = DockerWorkflowLauncher.BuildDockerArgs(SimpleRequest(), settings);

        var list = args.ToList();
        Assert.That(list, Does.Contain("--network"));
        var networkIdx = list.IndexOf("--network");
        Assert.That(list[networkIdx + 1], Is.EqualTo("auxilia-net"));
    }

    [Test]
    public void WhenNetworkNameNull_NetworkFlagAbsentFromArgs()
    {
        var settings = new DockerWorkflowLauncherSettings { NetworkName = null };
        var args = DockerWorkflowLauncher.BuildDockerArgs(SimpleRequest(), settings);

        Assert.That(args, Does.Not.Contain("--network"));
    }

    [Test]
    public void WhenNetworkNameEmptyString_NetworkFlagAbsentFromArgs()
    {
        var settings = new DockerWorkflowLauncherSettings { NetworkName = "  " };
        var args = DockerWorkflowLauncher.BuildDockerArgs(SimpleRequest(), settings);

        Assert.That(args, Does.Not.Contain("--network"));
    }

    // ------------------------------------------------------------------ image placement

    [Test]
    public void ImageIsAlwaysLastArgument()
    {
        var settings = new DockerWorkflowLauncherSettings { NetworkName = "net" };
        var args = DockerWorkflowLauncher.BuildDockerArgs(
            SimpleRequest("my-image:tag", new Dictionary<string, string> { ["K"] = "V" }),
            settings);

        Assert.That(args[^1], Is.EqualTo("my-image:tag"));
    }

    // ------------------------------------------------------------------ env var injection

    [Test]
    public void WhenEnvVarsProvided_EachHasADashEFlag()
    {
        var env = new Dictionary<string, string>
        {
            ["KEY_A"] = "value_a",
            ["KEY_B"] = "value_b"
        };
        var settings = new DockerWorkflowLauncherSettings { NetworkName = null };
        var args = DockerWorkflowLauncher.BuildDockerArgs(SimpleRequest(env: env), settings).ToList();

        // Two -e flags expected
        Assert.That(args.Count(a => a == "-e"), Is.EqualTo(2));
    }

    [Test]
    public void WhenEnvVarContainsEquals_PassedAsSingleArgument()
    {
        // Value with an equals sign must not be split into separate args
        var env = new Dictionary<string, string> { ["URL"] = "http://host/path?a=1&b=2" };
        var settings = new DockerWorkflowLauncherSettings { NetworkName = null };
        var args = DockerWorkflowLauncher.BuildDockerArgs(SimpleRequest(env: env), settings).ToList();

        var eIdx = args.IndexOf("-e");
        Assert.That(eIdx, Is.GreaterThanOrEqualTo(0));
        // The value following -e must be a single string containing the full key=value
        Assert.That(args[eIdx + 1], Is.EqualTo("URL=http://host/path?a=1&b=2"));
    }

    [Test]
    public void WhenEnvVarValueContainsSpace_PassedAsSingleArgument()
    {
        var env = new Dictionary<string, string> { ["MSG"] = "hello world" };
        var settings = new DockerWorkflowLauncherSettings { NetworkName = null };
        var args = DockerWorkflowLauncher.BuildDockerArgs(SimpleRequest(env: env), settings).ToList();

        var eIdx = args.IndexOf("-e");
        Assert.That(args[eIdx + 1], Is.EqualTo("MSG=hello world"));
    }

    // ------------------------------------------------------------------ structure

    [Test]
    public void ArgsAlwaysStartWithRunAndDetachedFlag()
    {
        var settings = new DockerWorkflowLauncherSettings { NetworkName = null };
        var args = DockerWorkflowLauncher.BuildDockerArgs(SimpleRequest(), settings);

        Assert.That(args[0], Is.EqualTo("run"));
        Assert.That(args[1], Is.EqualTo("-d"));
    }
}


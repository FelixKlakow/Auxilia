using Auxilia.SteeringInstance.Workflows;

namespace Auxilia.SteeringInstance.Tests.Workflows;

/// <summary>
/// Path selection for the run-output bind source (containerized Steering Instance support):
/// without a host directory the local RunOutputDirectory is the bind source; with one, the
/// launcher gets the host view while the dispatcher keeps its container-local view.
/// </summary>
[TestFixture]
[Category("Unit")]
public class WorkflowDispatcherRunOutputBindTests
{
    private static readonly Guid InstanceId = Guid.Parse("0c8c93cc-8be9-4f78-b27b-7da3adcae912");

    [Test]
    public void WhenNoHostDirectoryConfigured_BindSourceIsLocalRunOutputDirectory()
    {
        var settings = new WorkflowDispatcherSettings { RunOutputDirectory = "/run-output" };

        var bind = WorkflowDispatcher.ResolveOutputDirectoryBind(settings, InstanceId);

        Assert.That(bind, Is.EqualTo(Path.Combine("/run-output", InstanceId.ToString("N"))));
    }

    [Test]
    public void WhenHostDirectoryConfigured_BindSourceIsHostView_NotLocalView()
    {
        var settings = new WorkflowDispatcherSettings
        {
            RunOutputDirectory = "/run-output",
            RunOutputHostDirectory = "/var/lib/auxilia/run-output"
        };

        var bind = WorkflowDispatcher.ResolveOutputDirectoryBind(settings, InstanceId);

        Assert.That(bind, Is.EqualTo($"/var/lib/auxilia/run-output/{InstanceId:N}"));
    }

    [Test]
    public void WhenHostDirectoryIsWindowsStyle_SeparatorStyleIsPreserved()
    {
        // A Linux SI container administered by a Windows-host Docker Desktop daemon: the
        // daemon interprets the bind source, so the Windows separators must survive.
        var settings = new WorkflowDispatcherSettings
        {
            RunOutputDirectory = "/run-output",
            RunOutputHostDirectory = @"C:\Temp\auxilia-run-output"
        };

        var bind = WorkflowDispatcher.ResolveOutputDirectoryBind(settings, InstanceId);

        Assert.That(bind, Is.EqualTo($@"C:\Temp\auxilia-run-output\{InstanceId:N}"));
    }

    [Test]
    public void WhenHostDirectoryHasTrailingSeparator_NoDoubledSeparatorInBindSource()
    {
        var settings = new WorkflowDispatcherSettings
        {
            RunOutputDirectory = "/run-output",
            RunOutputHostDirectory = "/var/lib/auxilia/run-output/"
        };

        var bind = WorkflowDispatcher.ResolveOutputDirectoryBind(settings, InstanceId);

        Assert.That(bind, Is.EqualTo($"/var/lib/auxilia/run-output/{InstanceId:N}"));
    }

    [Test]
    public void WhenHostDirectoryIsWhitespace_FallsBackToLocalRunOutputDirectory()
    {
        var settings = new WorkflowDispatcherSettings
        {
            RunOutputDirectory = "/run-output",
            RunOutputHostDirectory = "   "
        };

        var bind = WorkflowDispatcher.ResolveOutputDirectoryBind(settings, InstanceId);

        Assert.That(bind, Is.EqualTo(Path.Combine("/run-output", InstanceId.ToString("N"))));
    }
}

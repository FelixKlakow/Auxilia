using Auxilia.Slots.AzureDevOps;
using Auxilia.Workflows;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.AzureDevOps.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class AzureDevOpsWorkItemsSlotHandlerTests
{
    private static SlotConfiguration Configuration(params (string Key, string Value)[] settings)
        => new("tfs-account", settings.ToDictionary(s => s.Key, s => s.Value));

    [Test]
    public void Register_WorkItems_ResolvesAzureDevOpsAccess()
    {
        var services = new ServiceCollection();
        var handler = new AzureDevOpsWorkItemsSlotHandler();

        handler.Register(services, "work-items", typeof(IWorkItemAccess), Configuration(
            ("OrgUrl", "https://tfs.example.com/tfs/DefaultCollection"),
            ("token", "ado-pat-secret")));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.That(scope.ServiceProvider.GetRequiredService<IWorkItemAccess>(),
            Is.InstanceOf<AzureDevOpsWorkItemAccess>());
    }

    [Test]
    public void Register_WithoutTheTokenSetting_ThrowsAClearMessage()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new AzureDevOpsWorkItemsSlotHandler().Register(
                new ServiceCollection(), "work-items", typeof(IWorkItemAccess),
                Configuration(("OrgUrl", "https://tfs.example.com/tfs/DefaultCollection"))))!;

        Assert.That(error.Message, Does.Contain("'token'").And.Contain("tfs-account"));
    }

    [Test]
    public void Register_WithoutTheOrgUrlSetting_ThrowsAClearMessage()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new AzureDevOpsWorkItemsSlotHandler().Register(
                new ServiceCollection(), "work-items", typeof(IWorkItemAccess),
                Configuration(("token", "ado-pat-secret"))))!;

        Assert.That(error.Message, Does.Contain("'OrgUrl'").And.Contain("tfs-account"));
    }

    [Test]
    public void Register_UnknownSlot_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new AzureDevOpsWorkItemsSlotHandler().Register(
                new ServiceCollection(), "repository", typeof(IWorkItemAccess), Configuration()));
    }
}

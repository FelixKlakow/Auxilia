using Auxilia.Slots.GitHub;
using Auxilia.Workflows;
using Auxilia.Workflows.SourceControl;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.GitHub.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class GitHubRepositorySlotHandlerTests
{
    [Test]
    public void RepositorySlot_RegistersSourceControlAccess()
    {
        var services = new ServiceCollection();
        var handler = new GitHubRepositorySlotHandler();

        handler.Register(services, "repository", typeof(ISourceControlAccess),
            new SlotConfiguration("github-repository", new Dictionary<string, string>
            {
                ["RepositoryUrl"] = "https://github.com/acme/widget"
            }));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.That(scope.ServiceProvider.GetRequiredService<ISourceControlAccess>(),
            Is.InstanceOf<GitHubRepositoryAccess>());
    }

    [Test]
    public void MissingRepositoryUrl_FailsRegistration()
    {
        var handler = new GitHubRepositorySlotHandler();

        Assert.Throws<InvalidOperationException>(() => handler.Register(
            new ServiceCollection(), "repository", typeof(ISourceControlAccess),
            new SlotConfiguration("github-repository", new Dictionary<string, string>())));
    }

    [Test]
    public void UnknownSlot_Throws()
    {
        var handler = new GitHubRepositorySlotHandler();

        Assert.Throws<InvalidOperationException>(() => handler.Register(
            new ServiceCollection(), "no-such-slot", typeof(ISourceControlAccess),
            new SlotConfiguration("github-repository", new Dictionary<string, string>
            {
                ["RepositoryUrl"] = "https://github.com/acme/widget"
            })));
    }
}

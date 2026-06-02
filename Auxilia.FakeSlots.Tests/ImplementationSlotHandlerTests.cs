using Auxilia.FakeSlots.Implementation.AgentFailure;
using Auxilia.FakeSlots.Implementation.Happy;
using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TaskSource;
using Auxilia.Workflows.TestRunner;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.FakeSlots.Tests;

[TestFixture]
public class ImplementationSlotHandlerTests
{
    private static readonly SlotConfiguration HappyConfig = new("fake-implementation-happy", new Dictionary<string, string>());
    private static readonly SlotConfiguration AgentFailureConfig = new("fake-implementation-agent-failure", new Dictionary<string, string>());

    // ── Happy path: per-slot registration ──────────────────────────────────

    [Test]
    public void ImplementationHappy_RepositorySlot_RegistersKeyedISourceControlWriteAccess()
    {
        var services = new ServiceCollection();
        var handler = new ImplementationHappySlotHandler();
        handler.Register(services, "repository", typeof(ISourceControlWriteAccess), HappyConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<ISourceControlWriteAccess>("repository");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void ImplementationHappy_RepositorySlot_RegistersKeyedISourceControlAccess()
    {
        var services = new ServiceCollection();
        var handler = new ImplementationHappySlotHandler();
        handler.Register(services, "repository", typeof(ISourceControlWriteAccess), HappyConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<ISourceControlAccess>("repository");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void ImplementationHappy_TaskSourceSlot_RegistersKeyedITaskSourceAccess()
    {
        var services = new ServiceCollection();
        var handler = new ImplementationHappySlotHandler();
        handler.Register(services, "task-source", typeof(ITaskSourceAccess), HappyConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<ITaskSourceAccess>("task-source");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void ImplementationHappy_ImplementationAgentSlot_RegistersKeyedIAiAgent()
    {
        var services = new ServiceCollection();
        var handler = new ImplementationHappySlotHandler();
        handler.Register(services, "implementation-agent", typeof(IAiAgent), HappyConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<IAiAgent>("implementation-agent");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void ImplementationHappy_ReviewerAgentSlot_RegistersKeyedIAiAgent()
    {
        var services = new ServiceCollection();
        var handler = new ImplementationHappySlotHandler();
        handler.Register(services, "reviewer-agent", typeof(IAiAgent), HappyConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<IAiAgent>("reviewer-agent");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void ImplementationHappy_TestRunnerSlot_RegistersKeyedITestRunner()
    {
        var services = new ServiceCollection();
        var handler = new ImplementationHappySlotHandler();
        handler.Register(services, "test-runner", typeof(ITestRunner), HappyConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<ITestRunner>("test-runner");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void ImplementationHappy_PullRequestSlot_RegistersKeyedIPullRequestAccess()
    {
        var services = new ServiceCollection();
        var handler = new ImplementationHappySlotHandler();
        handler.Register(services, "pull-request", typeof(IPullRequestAccess), HappyConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<IPullRequestAccess>("pull-request");
        Assert.That(resolved, Is.Not.Null);
    }

    // ── AgentFailure: per-slot registration ────────────────────────────────

    [Test]
    public void AgentFailure_RepositorySlot_RegistersKeyedISourceControlWriteAccess()
    {
        var services = new ServiceCollection();
        var handler = new ImplementationAgentFailureSlotHandler();
        handler.Register(services, "repository", typeof(ISourceControlWriteAccess), AgentFailureConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<ISourceControlWriteAccess>("repository");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void AgentFailure_TaskSourceSlot_RegistersKeyedITaskSourceAccess()
    {
        var services = new ServiceCollection();
        var handler = new ImplementationAgentFailureSlotHandler();
        handler.Register(services, "task-source", typeof(ITaskSourceAccess), AgentFailureConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<ITaskSourceAccess>("task-source");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void AgentFailure_ReviewerAgentSlot_RegistersKeyedIAiAgent()
    {
        var services = new ServiceCollection();
        var handler = new ImplementationAgentFailureSlotHandler();
        handler.Register(services, "reviewer-agent", typeof(IAiAgent), AgentFailureConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<IAiAgent>("reviewer-agent");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void AgentFailure_TestRunnerSlot_RegistersKeyedITestRunner()
    {
        var services = new ServiceCollection();
        var handler = new ImplementationAgentFailureSlotHandler();
        handler.Register(services, "test-runner", typeof(ITestRunner), AgentFailureConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<ITestRunner>("test-runner");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void AgentFailure_PullRequestSlot_RegistersKeyedIPullRequestAccess()
    {
        var services = new ServiceCollection();
        var handler = new ImplementationAgentFailureSlotHandler();
        handler.Register(services, "pull-request", typeof(IPullRequestAccess), AgentFailureConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<IPullRequestAccess>("pull-request");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public async Task AgentFailure_IAiAgent_ThrowsOnOpenSession()
    {
        var services = new ServiceCollection();
        var handler = new ImplementationAgentFailureSlotHandler();
        handler.Register(services, "implementation-agent", typeof(IAiAgent), AgentFailureConfig);

        using var sp = services.BuildServiceProvider();
        var agent = sp.GetRequiredKeyedService<IAiAgent>("implementation-agent");

        var ex = Assert.ThrowsAsync<Exception>(async () =>
            await agent.OpenSessionAsync());
        Assert.That(ex!.Message, Is.EqualTo("Simulated agent failure"));
    }
}

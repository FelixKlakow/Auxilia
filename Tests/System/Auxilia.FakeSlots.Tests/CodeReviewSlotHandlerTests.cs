using Auxilia.CodeReview.Workflow;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Mcp;
using Auxilia.CodeReview.Workflow.Verdicts;
using Auxilia.FakeSlots.CodeReview.Happy;
using Auxilia.FakeSlots.CodeReview.WriteBackFailure;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.FakeSlots.Tests;

[TestFixture]
public class CodeReviewSlotHandlerTests
{
    private static readonly SlotConfiguration FakeConfig = new("fake-code-review-happy", new Dictionary<string, string>());

    // ── Happy path: per-slot registration ──────────────────────────────────

    [Test]
    public void CodeReviewHappy_RepositorySlot_RegistersNonKeyedISourceControlAccess()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewHappySlotHandler();
        handler.Register(services, "repository", typeof(ISourceControlAccess), FakeConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<ISourceControlAccess>();
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void CodeReviewHappy_PullRequestSlot_RegistersNonKeyedIPullRequestAccess()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewHappySlotHandler();
        handler.Register(services, "pull-request", typeof(IPullRequestAccess), FakeConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IPullRequestAccess>();
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void CodeReviewHappy_WorkItemsSlot_RegistersNonKeyedIWorkItemAccess()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewHappySlotHandler();
        handler.Register(services, "work-items", typeof(IWorkItemAccess), FakeConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IWorkItemAccess>();
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void CodeReviewHappy_PrimaryReviewerSlot_RegistersKeyedIAiAgent()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewHappySlotHandler();
        handler.Register(services, "primary-reviewer", typeof(IAiAgent), FakeConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<IAiAgent>("primary-reviewer");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void CodeReviewHappy_SecondaryReviewerSlot_RegistersKeyedIAiAgent()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewHappySlotHandler();
        handler.Register(services, "secondary-reviewer", typeof(IAiAgent), FakeConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<IAiAgent>("secondary-reviewer");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void CodeReviewHappy_WorkflowBootstrapSlot_RegistersCodeReviewWorkflowServices()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewHappySlotHandler();
        handler.Register(services, "workflow-bootstrap", typeof(object), FakeConfig);

        using var sp = services.BuildServiceProvider();
        var twoEyes = sp.GetRequiredService<TwoEyesConfiguration>();
        Assert.That(twoEyes.Enabled, Is.True);
        var writeBack = sp.GetRequiredService<WriteBackConfiguration>();
        Assert.That(writeBack.MinimumSeverity, Is.EqualTo(FindingSeverity.Info));
    }

    // ── Happy path: AI behavior ─────────────────────────────────────────────

    [Test]
    public async Task CodeReviewHappy_PrimaryReviewer_CallsRecordFindingAndFileVerdict()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewHappySlotHandler();
        handler.Register(services, "primary-reviewer", typeof(IAiAgent), FakeConfig);

        using var sp = services.BuildServiceProvider();
        var agent = sp.GetRequiredKeyedService<IAiAgent>("primary-reviewer");

        var sink = new CodeReviewResultSinkMcpTools("primary-reviewer");
        var options = new AiSessionOptions { CapabilityTools = [sink] };

        await using var session = await agent.OpenSessionAsync(options);
        await session.ExecuteAsync("Review the following file changes");

        var findings = sink.DrainFindings();
        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].FilePath, Is.EqualTo("src/Widget.cs"));
        Assert.That(findings[0].Severity, Is.EqualTo(FindingSeverity.Medium));

        var verdict = sink.TakeFileVerdict();
        Assert.That(verdict, Is.EqualTo(FileVerdict.Reviewed));
    }

    [Test]
    public async Task CodeReviewHappy_SecondaryReviewer_CallsRecordSecondaryVerdictApproved()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewHappySlotHandler();
        handler.Register(services, "secondary-reviewer", typeof(IAiAgent), FakeConfig);

        using var sp = services.BuildServiceProvider();
        var agent = sp.GetRequiredKeyedService<IAiAgent>("secondary-reviewer");

        var sink = new CodeReviewResultSinkMcpTools("secondary-reviewer");
        var options = new AiSessionOptions { CapabilityTools = [sink] };

        await using var session = await agent.OpenSessionAsync(options);
        await session.ExecuteAsync("Review this finding");

        var verdict = sink.TakeSecondaryVerdict();
        Assert.That(verdict, Is.EqualTo(SecondaryVerdict.Approved));
    }

    [Test]
    public async Task CodeReviewHappy_PullRequestAccess_LinksWorkItemFromLaunchContext()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewHappySlotHandler();
        handler.Register(services, "pull-request", typeof(IPullRequestAccess), FakeConfig);

        using var sp = services.BuildServiceProvider();
        var pr = sp.GetRequiredService<IPullRequestAccess>();

        Environment.SetEnvironmentVariable("WORKFLOW_CONTEXT__WORKITEMID", "mail-abc123");
        try
        {
            var linked = await pr.GetLinkedWorkItemsAsync();
            Assert.That(linked, Has.Count.EqualTo(1));
            Assert.That(linked[0].Id, Is.EqualTo("mail-abc123"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WORKFLOW_CONTEXT__WORKITEMID", null);
        }

        var withoutContext = await pr.GetLinkedWorkItemsAsync();
        Assert.That(withoutContext, Is.Empty,
            "Runs without a work-item launch context must keep behaving as before.");
    }

    [Test]
    public void CodeReviewHappy_WorkflowBootstrapSlot_EnablesWorkItemSummaryWriteBack()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewHappySlotHandler();
        handler.Register(services, "workflow-bootstrap", typeof(object), FakeConfig);

        using var sp = services.BuildServiceProvider();
        var writeBack = sp.GetRequiredService<WriteBackConfiguration>();
        Assert.That(writeBack.PostSummaryToWorkItems, Is.True);
    }

    // ── WriteBackFailure: per-slot registration ─────────────────────────────

    private static readonly SlotConfiguration WbfConfig = new("fake-code-review-write-back-failure", new Dictionary<string, string>());

    [Test]
    public void WriteBackFailure_RepositorySlot_RegistersNonKeyedISourceControlAccess()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewWriteBackFailureSlotHandler();
        handler.Register(services, "repository", typeof(ISourceControlAccess), WbfConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<ISourceControlAccess>();
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void WriteBackFailure_PullRequestSlot_RegistersNonKeyedIPullRequestAccess()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewWriteBackFailureSlotHandler();
        handler.Register(services, "pull-request", typeof(IPullRequestAccess), WbfConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IPullRequestAccess>();
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void WriteBackFailure_WorkItemsSlot_RegistersNonKeyedIWorkItemAccess()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewWriteBackFailureSlotHandler();
        handler.Register(services, "work-items", typeof(IWorkItemAccess), WbfConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IWorkItemAccess>();
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void WriteBackFailure_PrimaryReviewerSlot_RegistersKeyedIAiAgent()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewWriteBackFailureSlotHandler();
        handler.Register(services, "primary-reviewer", typeof(IAiAgent), WbfConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<IAiAgent>("primary-reviewer");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void WriteBackFailure_SecondaryReviewerSlot_RegistersKeyedIAiAgent()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewWriteBackFailureSlotHandler();
        handler.Register(services, "secondary-reviewer", typeof(IAiAgent), WbfConfig);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredKeyedService<IAiAgent>("secondary-reviewer");
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void WriteBackFailure_WorkflowBootstrapSlot_RegistersCodeReviewWorkflowServices()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewWriteBackFailureSlotHandler();
        handler.Register(services, "workflow-bootstrap", typeof(object), WbfConfig);

        using var sp = services.BuildServiceProvider();
        var twoEyes = sp.GetRequiredService<TwoEyesConfiguration>();
        Assert.That(twoEyes.Enabled, Is.True);
        var writeBack = sp.GetRequiredService<WriteBackConfiguration>();
        Assert.That(writeBack.MinimumSeverity, Is.EqualTo(FindingSeverity.Info));
    }

    [Test]
    public async Task WriteBackFailure_PullRequestAccess_ThrowsOnPostComments()
    {
        var services = new ServiceCollection();
        var handler = new CodeReviewWriteBackFailureSlotHandler();
        handler.Register(services, "pull-request", typeof(IPullRequestAccess), WbfConfig);

        using var sp = services.BuildServiceProvider();
        var pr = sp.GetRequiredService<IPullRequestAccess>();

        var ex = Assert.ThrowsAsync<Exception>(async () =>
            await pr.PostCommentAsync("some comment"));
        Assert.That(ex!.Message, Is.EqualTo("Simulated write-back failure"));
    }
}

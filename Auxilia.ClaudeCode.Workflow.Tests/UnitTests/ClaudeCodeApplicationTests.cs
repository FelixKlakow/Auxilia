using Auxilia.ClaudeCode.Workflow;
using Auxilia.Workflows.Views;
using Moq;

namespace Auxilia.ClaudeCode.Workflow.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class ClaudeCodeApplicationTests
{
    private string _outputDir = "";
    private ClaudeCodeRunContext _context = null!;
    private readonly TimeProvider _time = TimeProvider.System;

    [SetUp]
    public void SetUp()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), $"cc-app-{Guid.NewGuid():N}");
        _context = new ClaudeCodeRunContext("Fix the bug", "/workspace", _outputDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }

    [Test]
    public async Task SuccessfulRun_PublishesInstructionAgentTurnsProgressAndReport()
    {
        var agentTurn = new AgentChatEntry(AgentChatRole.Assistant, "On it.", _time.GetUtcNow());
        var agent = new Mock<ICodingAgent>(MockBehavior.Strict);
        agent.Setup(a => a.RunAsync(
                new CodingAgentRequest("Fix the bug", "/workspace"),
                It.IsAny<Func<AgentChatEntry, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (CodingAgentRequest _,
                Func<AgentChatEntry, CancellationToken, Task> onEntry,
                CancellationToken ct) =>
            {
                await onEntry(agentTurn, ct);
                return new CodingAgentResult(true, "Fixed.", 3, 0.01m, 1800);
            });

        var chatEntries = new List<AgentChatEntry>();
        var progressEntries = new List<SessionProgressEntry>();
        var views = new Mock<IViewPublisher>(MockBehavior.Strict);
        views.Setup(v => v.PublishAsync(
                ClaudeCodeApplication.ChatViewName, It.IsAny<AgentChatEntry>(), It.IsAny<CancellationToken>()))
            .Callback((string _, AgentChatEntry entry, CancellationToken _) => chatEntries.Add(entry))
            .Returns(Task.CompletedTask);
        views.Setup(v => v.PublishAsync(
                ClaudeCodeApplication.ProgressViewName, It.IsAny<SessionProgressEntry>(), It.IsAny<CancellationToken>()))
            .Callback((string _, SessionProgressEntry entry, CancellationToken _) => progressEntries.Add(entry))
            .Returns(Task.CompletedTask);

        await new ClaudeCodeApplication(agent.Object, views.Object, _context, _time)
            .RunAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(chatEntries, Has.Count.EqualTo(2),
                "The instruction and the agent turn must reach the chat view.");
            Assert.That(chatEntries[0].Role, Is.EqualTo(AgentChatRole.User));
            Assert.That(chatEntries[0].Content, Is.EqualTo("Fix the bug"));
            Assert.That(chatEntries[1], Is.EqualTo(agentTurn));
            Assert.That(progressEntries.Select(p => (p.Phase, p.Message)), Is.EqualTo(new[]
            {
                ("session", "started"), ("session", "completed"), ("report", "written")
            }));
            Assert.That(File.Exists(Path.Combine(_outputDir, SessionReportWriter.FileName)), Is.True);
        });
    }

    [Test]
    public void FailedRun_StillWritesTheReport_ThenThrows()
    {
        var agent = new Mock<ICodingAgent>(MockBehavior.Strict);
        agent.Setup(a => a.RunAsync(
                It.IsAny<CodingAgentRequest>(),
                It.IsAny<Func<AgentChatEntry, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodingAgentResult(false, null, ErrorMessage: "max turns exceeded"));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ClaudeCodeApplication(agent.Object, views: null, _context, _time)
                .RunAsync(CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Is.EqualTo("max turns exceeded"));
            Assert.That(File.Exists(Path.Combine(_outputDir, SessionReportWriter.FileName)), Is.True,
                "The report is the durable record — it must exist for failed runs too.");
        });
    }

    [Test]
    public async Task WithoutViewPublisher_RunsToCompletion()
    {
        var agent = new Mock<ICodingAgent>(MockBehavior.Strict);
        agent.Setup(a => a.RunAsync(
                It.IsAny<CodingAgentRequest>(),
                It.IsAny<Func<AgentChatEntry, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodingAgentResult(true, "Done."));

        await new ClaudeCodeApplication(agent.Object, views: null, _context, _time)
            .RunAsync(CancellationToken.None);

        Assert.That(File.Exists(Path.Combine(_outputDir, SessionReportWriter.FileName)), Is.True);
    }
}

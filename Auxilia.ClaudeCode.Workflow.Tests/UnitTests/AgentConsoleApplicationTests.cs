using System.Text.Json;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.Views;

namespace Auxilia.ClaudeCode.Workflow.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class AgentConsoleApplicationTests
{
    private sealed class FakeSessionHost : ISessionHost
    {
        private readonly TaskCompletionSource _sessionEnded = new();
        public List<string> Journal { get; } = [];
        public TerminalSessionInfo? Started { get; private set; }
        public bool EndsOnItsOwn { get; init; } = true;

        public Task StartAsync(TerminalSessionInfo session, CancellationToken ct)
        {
            Journal.Add("start");
            Started = session;
            if (EndsOnItsOwn)
                _sessionEnded.SetResult();
            return Task.CompletedTask;
        }

        public async Task WaitForSessionEndAsync(CancellationToken ct)
        {
            Journal.Add("wait");
            await _sessionEnded.Task.WaitAsync(ct);
        }

        public Task ShutdownAsync()
        {
            Journal.Add("shutdown");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingViews : IViewPublisher
    {
        public List<(string View, object Item)> Published { get; } = [];

        public Task PublishAsync<TItem>(string viewName, TItem item, CancellationToken ct = default)
        {
            Published.Add((viewName, item!));
            return Task.CompletedTask;
        }
    }

    private string _outputDir = null!;

    [SetUp]
    public void SetUp()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), $"console-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outputDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }

    private AgentSessionContext Context(string instruction = "Fix the build")
        => AgentSessionContext.FromValues(
            instruction.Length > 0 ? instruction : null, null,
            Path.GetTempPath(), _outputDir, viewMode: AgentViewModes.Console);

    private sealed class RecordingPreparer : IConsoleSessionPreparer
    {
        public int Calls { get; private set; }

        public Task PrepareAsync(string workspaceDirectory, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task Run_StartsWaitsShutsDown_AndWritesTheSessionReport()
    {
        var host = new FakeSessionHost();
        var views = new RecordingViews();
        var preparer = new RecordingPreparer();

        await new AgentConsoleApplication(
                host, views, Context(),
                new CodingAgentCredentials("sk-ant-oat-token", null, "claude", null),
                TimeProvider.System, preparer)
            .RunAsync(CancellationToken.None);

        var report = JsonSerializer.Deserialize<SessionReport>(
            await File.ReadAllTextAsync(Path.Combine(_outputDir, SessionReportWriter.FileName)));
        Assert.Multiple(() =>
        {
            Assert.That(host.Journal, Is.EqualTo(new[] { "start", "wait", "shutdown" }));
            Assert.That(preparer.Calls, Is.EqualTo(1),
                "the provider's session preparation (interactive login) runs before the host starts");
            Assert.That(report!.Success, Is.True);
            Assert.That(report.Instruction, Is.EqualTo("Fix the build"));
            Assert.That(views.Published.Select(p => p.View),
                Does.Contain(AgentSessionApplication.ChatViewName).And
                    .Contain(AgentSessionApplication.ProgressViewName),
                "the instruction and the console hand-off note land on the conversation view");
        });
    }

    [Test]
    public void BuildSession_CarriesCredentialAndInstruction_OnlyInTheEnvironment()
    {
        var session = AgentConsoleApplication.BuildSession(
            Context("Do \"quoted\" things"),
            new CodingAgentCredentials("sk-ant-oat-secret", null, "claude", null));

        Assert.Multiple(() =>
        {
            Assert.That(session.Command,
                Is.EqualTo($"claude \"${AgentConsoleApplication.InstructionVariable}\""),
                "the command references the instruction variable — run data never rides the command line");
            Assert.That(session.Environment[AgentConsoleApplication.InstructionVariable],
                Is.EqualTo("Do \"quoted\" things"));
            Assert.That(session.Environment["CLAUDE_CODE_OAUTH_TOKEN"], Is.EqualTo("sk-ant-oat-secret"));
            Assert.That(session.TerminalPort, Is.EqualTo(AgentConsoleApplication.TerminalPort));
        });
    }

    [Test]
    public void BuildSession_WithoutInstructionOrCredentials_RunsThePlainCli()
    {
        var session = AgentConsoleApplication.BuildSession(Context(instruction: ""), credentials: null);

        Assert.Multiple(() =>
        {
            Assert.That(session.Command, Is.EqualTo("claude"));
            Assert.That(session.Environment, Is.Empty);
        });
    }
}

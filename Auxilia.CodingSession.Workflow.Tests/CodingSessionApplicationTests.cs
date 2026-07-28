using System.Text.Json;
using Auxilia.CodingSession.Workflow;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.Views;
using Moq;

namespace Auxilia.CodingSession.Workflow.Tests;

[TestFixture]
[Category("Unit")]
public class CodingSessionApplicationTests
{
    private sealed class FakeSessionHost : ISessionHost
    {
        private readonly TaskCompletionSource _sessionEnded = new();
        public List<string> Journal { get; } = [];
        public bool EndsOnItsOwn { get; init; } = true;

        public Task StartAsync(TerminalSessionInfo session, CancellationToken ct)
        {
            Journal.Add("start");
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

    private sealed class FakeGitRunner : IGitRunner
    {
        public List<string> Commands { get; } = [];
        public Dictionary<string, (int ExitCode, string Output)> Responses { get; } =
            new(StringComparer.Ordinal);

        public Task<(int ExitCode, string Output)> RunAsync(
            string workingDirectory, string arguments, CancellationToken ct)
        {
            Commands.Add(arguments);
            var response = Responses
                .FirstOrDefault(r => arguments.StartsWith(r.Key, StringComparison.Ordinal));
            return Task.FromResult(response.Key is null ? (0, "") : response.Value);
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

    private static SessionRunContext Context(string outputDir, int maxMinutes = 240)
        => new(
            Path.Combine(Path.GetTempPath(), $"cs-ws-{Guid.NewGuid():N}"),
            outputDir,
            "claude",
            SessionRunContext.DefaultTerminalPort,
            TimeSpan.FromMinutes(maxMinutes),
            "cc-session/test1234");

    private string _outputDir = null!;

    [SetUp]
    public void SetUp()
        => _outputDir = Path.Combine(Path.GetTempPath(), $"cs-out-{Guid.NewGuid():N}");

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }

    [Test]
    public async Task Run_BranchesWaitsCollectsAndWritesTheResultArtifact()
    {
        var host = new FakeSessionHost();
        var git = new FakeGitRunner();
        git.Responses["rev-parse HEAD"] = (0, "abc123\n");
        git.Responses["diff --name-only abc123 HEAD"] = (0, "src/One.cs\nsrc/Two.cs\n");
        git.Responses["status --porcelain"] = (0, " M src/Two.cs\n?? NOTES.md\n");
        var views = new RecordingViews();

        var result = await new CodingSessionApplication(
                host, git, views, Context(_outputDir), TimeProvider.System)
            .RunAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(host.Journal, Is.EqualTo(new[] { "start", "wait", "shutdown" }),
                "the session host is shut down exactly once, after the CLI exits");
            Assert.That(git.Commands, Does.Contain("checkout -b cc-session/test1234"));
            Assert.That(result.ChangedFiles, Is.EqualTo(new[] { "NOTES.md", "src/One.cs", "src/Two.cs" }),
                "committed and uncommitted changes merge, deduplicated");
            Assert.That(result.TimedOut, Is.False);
            Assert.That(views.Published.Select(p => p.View), Is.All.EqualTo("progress"));

            var written = JsonSerializer.Deserialize<CodingSessionResult>(
                File.ReadAllText(Path.Combine(_outputDir, CodingSessionResult.FileName)));
            Assert.That(written!.Branch, Is.EqualTo("cc-session/test1234"));
            Assert.That(written.ChangedFiles, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public async Task Run_MaterializesMailAttachments_IntoTheWorkspace()
    {
        var context = Context(_outputDir);
        Directory.CreateDirectory(context.WorkspaceDirectory);
        var workItems = new Moq.Mock<Auxilia.Workflows.TaskSource.IWorkItemAccess>();
        workItems
            .Setup(w => w.GetAttachmentsAsync("mail-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Auxilia.Workflows.TaskSource.WorkItemAttachment(
                "../evil/spec.pdf", [1, 2, 3])]);
        Environment.SetEnvironmentVariable("WORKFLOW_CONTEXT__WORKITEMID", "mail-42");
        try
        {
            await new CodingSessionApplication(
                    new FakeSessionHost(), new FakeGitRunner(), null, context,
                    TimeProvider.System, workItems.Object)
                .RunAsync(CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WORKFLOW_CONTEXT__WORKITEMID", null);
        }

        var written = Path.Combine(
            context.WorkspaceDirectory, CodingSessionApplication.AttachmentDirectoryName, "spec.pdf");
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(written), "the attachment lands in the workspace");
            Assert.That(File.ReadAllBytes(written), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(Path.GetFileName(written), Is.EqualTo("spec.pdf"),
                "path components in attachment names are stripped — no traversal");
        });
        Directory.Delete(context.WorkspaceDirectory, recursive: true);
    }

    [Test]
    public async Task Run_WhenTheSessionOutlivesMaxDuration_EndsItAndMarksTimedOut()
    {
        var host = new FakeSessionHost { EndsOnItsOwn = false };
        var git = new FakeGitRunner();
        var context = Context(_outputDir) with { MaxDuration = TimeSpan.FromMilliseconds(50) };

        var result = await new CodingSessionApplication(
                host, git, null, context, TimeProvider.System)
            .RunAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.TimedOut, Is.True);
            Assert.That(host.Journal, Does.Contain("shutdown"),
                "the guard still tears the session down cleanly");
        });
    }
}

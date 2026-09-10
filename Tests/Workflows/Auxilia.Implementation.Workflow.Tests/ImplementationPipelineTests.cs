using Auxilia.Implementation.Workflow;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TaskSource;

namespace Auxilia.Implementation.Workflow.Tests;

[TestFixture, Category("Unit")]
public sealed class ImplementationPipelineTests
{
    /// <summary>
    /// Host + event source in one: each driven prompt runs a script (writing the exchange
    /// files an agent would) and immediately completes the turn. Broadcasts to every
    /// registered handler like the production event hub — a run may hold several consoles.
    /// </summary>
    private sealed class ScriptedConsole : ISessionHost, IConsoleSessionEventSource
    {
        private readonly List<Func<ConsoleSessionEvent, CancellationToken, Task>> _handlers = [];

        public List<string> Drives { get; } = [];
        public List<string> StartedSessions { get; } = [];
        public required Action<string> OnDrive { get; init; }

        public Task StartAsync(TerminalSessionInfo session, CancellationToken ct)
        {
            StartedSessions.Add(session.SessionName);
            return Task.CompletedTask;
        }

        public Task StartAsync(
            Func<ConsoleSessionEvent, CancellationToken, Task> onEvent, CancellationToken ct)
        {
            _handlers.Add(onEvent);
            return Task.CompletedTask;
        }

        public Task WaitForSessionEndAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task SendTextAsync(string text, CancellationToken ct)
        {
            Drives.Add(text);
            OnDrive(text);
            foreach (var handler in _handlers.ToArray())
                await handler(new ConsoleSessionEvent(ConsoleSessionEvent.TurnEnded, "done"), ct);
        }

        public Task ShutdownAsync() => Task.CompletedTask;

        public ValueTask StopAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWorkItems : IWorkItemAccess
    {
        public WorkItem? Item { get; init; }
        public IReadOnlyList<string> States { get; init; } = [];
        public List<(string Id, string State)> SetStates { get; } = [];
        public List<string> Comments { get; } = [];

        public Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken ct = default)
            => Task.FromResult(Item?.Id == id ? Item : null);

        public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(
            IEnumerable<string> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkItem>>(Item is null ? [] : [Item]);

        public Task PostCommentAsync(string id, string comment, CancellationToken ct = default)
        {
            Comments.Add(comment);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetStatesAsync(string id, CancellationToken ct = default)
            => Task.FromResult(States);

        public Task SetStateAsync(string id, string state, CancellationToken ct = default)
        {
            SetStates.Add((id, state));
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedReviewer : IReviewRunner
    {
        private readonly Queue<string> _verdicts;
        public List<string> Instructions { get; } = [];

        public ScriptedReviewer(params string[] verdicts) => _verdicts = new Queue<string>(verdicts);

        public Task<ReviewVerdict> ReviewAsync(string instruction, string verdictPath, CancellationToken ct)
        {
            Instructions.Add(instruction);
            var verdict = _verdicts.Count > 0 ? _verdicts.Dequeue() : "APPROVE";
            File.WriteAllText(verdictPath, verdict);
            return Task.FromResult(ReviewVerdict.FromFile(verdictPath));
        }
    }

    private sealed class FakeGit : IGitRunner
    {
        public List<string> Commands { get; } = [];

        /// <summary>What <c>rev-parse --git-path info/exclude</c> answers (null = not a repository).</summary>
        public string? ExcludePath { get; init; }

        public Task<(int ExitCode, string Output)> RunAsync(
            string workingDirectory, string arguments, CancellationToken ct)
        {
            Commands.Add(arguments);
            if (arguments.StartsWith("rev-parse"))
                return Task.FromResult(ExcludePath is null ? (128, "fatal: not a git repository") : (0, ExcludePath + "\n"));
            return Task.FromResult((0, arguments.StartsWith("diff") ? "+new line" : ""));
        }
    }

    private string _workspace = null!;
    private string _output = null!;

    [SetUp]
    public void SetUp()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"impl-ws-{Guid.NewGuid():N}");
        _output = Path.Combine(Path.GetTempPath(), $"impl-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_output);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_workspace, recursive: true);
        Directory.Delete(_output, recursive: true);
    }

    private ImplementationContext Context(string? workspace = null) => ImplementationContext.FromValues(
        "42", workspace ?? _workspace, _output,
        pushMode: PushModes.Auto, targetState: "Testing");

    /// <summary>Runs the whole pipeline with an approving reviewer; the agent script writes the exchange files.</summary>
    private async Task RunToPushAsync(FakeGit git, ImplementationContext context)
    {
        var console = new ScriptedConsole
        {
            OnDrive = prompt =>
            {
                if (prompt.Contains("questions", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(Path.Combine(context.ExchangeDirectory, "questions.md"), "NONE");
                if (prompt.Contains("plan", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(Path.Combine(context.ExchangeDirectory, "plan.md"), "# Plan\n- step");
            }
        };
        var workItems = new FakeWorkItems
        {
            Item = new WorkItem("42", "Add login page", "As a user…", "Active", null, []),
            States = ["New", "Active", "Testing", "Done"],
        };
        await new ImplementationPipeline(
                workItems, null, null, context, Pool(context, console), new ScriptedReviewer(), git, TimeProvider.System)
            .RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task Push_StagesTheWholeTree_MinusTheExchangeFiles()
    {
        var git = new FakeGit { ExcludePath = Path.Combine(_workspace, ".git", "info", "exclude") };

        await RunToPushAsync(git, Context());

        var stage = git.Commands.Single(c => c.StartsWith("add "));
        Assert.Multiple(() =>
        {
            Assert.That(stage, Is.EqualTo("add -A -- :/ :(exclude).auxilia :(exclude).mcp.json"),
                "the exchange directory and the MCP registration never reach a commit, exclude file or not");
            Assert.That(git.Commands.IndexOf(stage), Is.LessThan(git.Commands.FindIndex(c => c.StartsWith("commit "))));
        });
    }

    [Test]
    public async Task Exclude_IsWrittenIntoTheRepositoryGitDir_EvenAboveTheWorkspace()
    {
        // A mount working directory: the workspace is a directory BELOW the repository root.
        var workspace = Path.Combine(_workspace, "src", "app");
        Directory.CreateDirectory(workspace);
        var excludePath = Path.Combine(_workspace, ".git", "info", "exclude");
        var git = new FakeGit { ExcludePath = excludePath };

        await RunToPushAsync(git, Context(workspace));

        Assert.Multiple(() =>
        {
            Assert.That(git.Commands, Does.Contain("rev-parse --git-path info/exclude"),
                "the exclude location comes from git, not from a .git directory lookup");
            Assert.That(File.Exists(excludePath), Is.True, "the exclude lands in the repository's git dir");
            Assert.That(File.ReadAllText(excludePath), Does.Contain(".auxilia/").And.Contain(".mcp.json"));
        });
    }

    [Test]
    public async Task Exclude_RelativeGitPath_ResolvesAgainstTheWorkspace()
    {
        // git answers a RELATIVE path when the workspace is the repository root.
        var git = new FakeGit { ExcludePath = ".git/info/exclude" };

        await RunToPushAsync(git, Context());

        Assert.That(File.ReadAllText(Path.Combine(_workspace, ".git", "info", "exclude")), Does.Contain(".auxilia/"));
    }

    [Test]
    public async Task Exclude_NotARepository_WritesNothing()
    {
        var git = new FakeGit { ExcludePath = null };

        await RunToPushAsync(git, Context());

        Assert.That(Directory.Exists(Path.Combine(_workspace, ".git")), Is.False);
    }

    private static AgentConsolePool Pool(ImplementationContext context, ScriptedConsole console)
        => new(context, null, null, () => console, () => console, null);

    [Test]
    public async Task HappyPath_PlansImplementsReviewsPushesAndClosesTheStory()
    {
        var console = new ScriptedConsole
        {
            OnDrive = prompt =>
            {
                // The scripted "agent": refinement finds nothing open; plan and revisions
                // land in the exchange files, exactly like a real CLI following the drive.
                if (prompt.Contains("questions", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(Path.Combine(_workspace, ".auxilia", "questions.md"), "NONE");
                if (prompt.Contains("plan", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(Path.Combine(_workspace, ".auxilia", "plan.md"), "# Plan\n- step");
            }
        };
        var workItems = new FakeWorkItems
        {
            Item = new WorkItem("42", "Add login page", "As a user…", "Active", null, []),
            States = ["New", "Active", "Testing", "Done"],
        };
        // Plan review asks for ONE revision, code review approves — both loops exercised.
        var reviewer = new ScriptedReviewer("REVISE\nplan needs tests", "APPROVE\nplan good", "APPROVE\nship it");
        var git = new FakeGit();
        var context = Context();

        await new ImplementationPipeline(
                workItems, null, null, context, Pool(context, console), reviewer, git, TimeProvider.System)
            .RunAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(console.Drives.Any(d => d.Contains("complete and unambiguous", StringComparison.Ordinal)),
                Is.True, "the refinement check drove the console");
            Assert.That(console.Drives.Any(d => d.Contains("Rework", StringComparison.Ordinal)),
                Is.True, "the plan-review REVISE verdict drove a refinement");
            Assert.That(console.Drives.Any(d => d.Contains("Implement the approved plan")),
                Is.True);
            Assert.That(console.Drives.All(d => !d.Contains('\n')),
                Is.True, "drives are single-line — one input per turn");
            Assert.That(git.Commands.Any(c => c.StartsWith("checkout -b impl/42-add-login-page")), Is.True);
            Assert.That(git.Commands.Any(c => c.StartsWith("push -u origin impl/42-add-login-page")), Is.True,
                "push-mode auto pushes without a gate");
            Assert.That(workItems.SetStates, Is.EqualTo(new[] { ("42", "Testing") }),
                "the target state resolves against the source's OWN vocabulary");
            Assert.That(workItems.Comments, Has.Count.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(_output, "review-bundle.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(_output, "plan.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(_output, SessionReportWriter.FileName)), Is.True);
        });
    }

    [Test]
    public void MissingWorkItem_FailsLoudly()
    {
        var console = new ScriptedConsole { OnDrive = _ => { } };
        var context = Context();
        var pipeline = new ImplementationPipeline(
            new FakeWorkItems(), null, null, context,
            Pool(context, console), null, new FakeGit(), TimeProvider.System);

        Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.RunAsync(CancellationToken.None));
    }

    [Test]
    public async Task SkipMode_NeverPushes_AndAnUnboundReviewerSkipsAiReview()
    {
        var console = new ScriptedConsole
        {
            OnDrive = prompt =>
            {
                if (prompt.Contains("plan", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(Path.Combine(_workspace, ".auxilia", "plan.md"), "# Plan");
            }
        };
        var workItems = new FakeWorkItems
        {
            Item = new WorkItem("42", "Fix bug", null, "Active", null, []),
        };
        var git = new FakeGit();
        var context = ImplementationContext.FromValues(
            "42", _workspace, _output,
            refinementCheck: "false", pushMode: PushModes.Skip);

        await new ImplementationPipeline(
                workItems, null, null, context,
                Pool(context, console), reviewer: null, git, TimeProvider.System)
            .RunAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(git.Commands.Any(c => c.StartsWith("push")), Is.False);
            Assert.That(workItems.SetStates, Is.Empty, "no states at the source, none set");
            Assert.That(workItems.Comments.Single(), Does.Contain("without a push"));
        });
    }

    [Test]
    public async Task FreshPlanAgent_RunsInItsOwnSession_AndRefinerReviews()
    {
        // plan-agent=fresh: planning and implementation (shared with plan) run in a SECOND
        // tmux session; review-by=refinement-agent: reviews drive the AUTHOR console.
        var console = new ScriptedConsole
        {
            OnDrive = prompt =>
            {
                if (prompt.Contains("questions", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(Path.Combine(_workspace, ".auxilia", "questions.md"), "NONE");
                if (prompt.Contains("plan.md", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(Path.Combine(_workspace, ".auxilia", "plan.md"), "# Plan");
                if (prompt.Contains("verdict", StringComparison.OrdinalIgnoreCase))
                {
                    var target = prompt.Contains("code-review", StringComparison.OrdinalIgnoreCase)
                        ? "code-review.md" : "plan-review.md";
                    File.WriteAllText(Path.Combine(_workspace, ".auxilia", target), "APPROVE\nfine");
                }
            }
        };
        var workItems = new FakeWorkItems
        {
            Item = new WorkItem("42", "Fix bug", null, "Active", null, []),
        };
        var context = ImplementationContext.FromValues(
            "42", _workspace, _output,
            pushMode: PushModes.Skip, planAgent: StageAgents.Fresh,
            reviewBy: ReviewAgents.RefinementAgent);

        await new ImplementationPipeline(
                workItems, null, null, context,
                Pool(context, console), reviewer: null, new FakeGit(), TimeProvider.System)
            .RunAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(console.StartedSessions, Does.Contain("agent-session"),
                "the author console starts first");
            Assert.That(console.StartedSessions, Does.Contain("plan-console"),
                "plan-agent=fresh starts a second session");
            Assert.That(console.Drives.Any(d => d.Contains("APPROVE or REVISE")),
                Is.True, "review-by=refinement-agent drives the review into a console");
        });
    }
}

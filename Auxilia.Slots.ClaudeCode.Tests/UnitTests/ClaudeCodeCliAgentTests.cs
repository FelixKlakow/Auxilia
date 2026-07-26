using System.Diagnostics;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Slots.ClaudeCode;
using Auxilia.Workflows.Views;

namespace Auxilia.Slots.ClaudeCode.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class ClaudeCodeCliAgentTests
{
    private static readonly CodingAgentRequest Request = new("Fix the bug", "/workspace");

    private static ClaudeCodeCliOptions Options(string apiKey = "sk-test", string? model = null)
        => new() { ApiKey = apiKey, Model = model, MaxTurns = 7, CliPath = "claude" };

    [Test]
    public void BuildStartInfo_PassesKeyOnlyThroughEnvironment_NeverAsArgument()
    {
        var agent = new ClaudeCodeCliAgent(Options(apiKey: "sk-super-secret"));

        var startInfo = agent.BuildStartInfo(Request);

        Assert.Multiple(() =>
        {
            Assert.That(startInfo.Environment["ANTHROPIC_API_KEY"], Is.EqualTo("sk-super-secret"));
            Assert.That(startInfo.ArgumentList, Has.None.Contains("sk-super-secret"));
            Assert.That(startInfo.FileName, Does.Not.Contain("sk-super-secret"));
        });
    }

    [Test]
    public void BuildStartInfo_AccountTokenWinsOverApiKey_AndStaysOffTheCommandLine()
    {
        var agent = new ClaudeCodeCliAgent(new ClaudeCodeCliOptions
        {
            OAuthToken = "sk-ant-oat-secret", ApiKey = "sk-fallback", CliPath = "claude"
        });

        var startInfo = agent.BuildStartInfo(Request);

        Assert.Multiple(() =>
        {
            Assert.That(startInfo.Environment["CLAUDE_CODE_OAUTH_TOKEN"], Is.EqualTo("sk-ant-oat-secret"));
            // ProcessStartInfo.Environment inherits the machine environment — assert the
            // fallback key was not exported rather than that the variable is absent.
            startInfo.Environment.TryGetValue("ANTHROPIC_API_KEY", out var apiKey);
            Assert.That(apiKey, Is.Not.EqualTo("sk-fallback"), "only the credential in use may be exported");
            Assert.That(startInfo.ArgumentList, Has.None.Contains("sk-ant-oat-secret"));
        });
    }

    [Test]
    public void BuildStartInfo_UsesHeadlessStreamJsonArguments()
    {
        var agent = new ClaudeCodeCliAgent(Options());

        var startInfo = agent.BuildStartInfo(Request);

        Assert.Multiple(() =>
        {
            Assert.That(startInfo.FileName, Is.EqualTo("claude"));
            Assert.That(startInfo.WorkingDirectory, Is.EqualTo("/workspace"));
            Assert.That(startInfo.ArgumentList, Does.Contain("-p"));
            Assert.That(startInfo.ArgumentList, Does.Contain("Fix the bug"));
            Assert.That(startInfo.ArgumentList, Does.Contain("stream-json"));
            Assert.That(startInfo.ArgumentList, Does.Contain("--verbose"));
            Assert.That(startInfo.ArgumentList, Does.Contain("--max-turns"));
            Assert.That(startInfo.ArgumentList, Does.Contain("7"));
            Assert.That(startInfo.ArgumentList, Does.Contain("--dangerously-skip-permissions"));
            Assert.That(startInfo.ArgumentList, Does.Not.Contain("--model"));
            Assert.That(startInfo.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"], Is.EqualTo("1"));
            Assert.That(startInfo.Environment["IS_SANDBOX"], Is.EqualTo("1"),
                "skip-permissions as root works only when the CLI knows it runs sandboxed");
        });
    }

    [Test]
    public void BuildStartInfo_WithoutMaxTurns_PassesNoCap()
    {
        var agent = new ClaudeCodeCliAgent(new ClaudeCodeCliOptions { ApiKey = "sk-test" });

        var startInfo = agent.BuildStartInfo(Request);

        Assert.That(startInfo.ArgumentList, Does.Not.Contain("--max-turns"),
            "the agent runs uncapped unless a turn limit is explicitly configured");
    }

    [Test]
    public void BuildStartInfo_WithModelOverride_AddsModelArgument()
    {
        var agent = new ClaudeCodeCliAgent(Options(model: "claude-sonnet-4-6"));

        var startInfo = agent.BuildStartInfo(Request);

        var arguments = startInfo.ArgumentList.ToList();
        Assert.That(arguments, Does.Contain("--model"));
        Assert.That(arguments[arguments.IndexOf("--model") + 1], Is.EqualTo("claude-sonnet-4-6"));
    }

    [Test]
    public async Task RunAsync_StreamsEntriesAndReturnsSuccessResult()
    {
        var factory = new FakeProcessFactory(
            """
            {"type":"system","subtype":"init","model":"claude-stub"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Working."}]}}
            {"type":"result","subtype":"success","is_error":false,"duration_ms":10,"num_turns":2,"result":"Done.","total_cost_usd":0.01}
            """,
            exitCode: 0);
        var agent = new ClaudeCodeCliAgent(Options(), factory);
        var entries = new List<AgentChatEntry>();

        var result = await agent.RunAsync(Request, (entry, _) =>
        {
            entries.Add(entry);
            return Task.CompletedTask;
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.Summary, Is.EqualTo("Done."));
            Assert.That(result.TurnCount, Is.EqualTo(2));
            Assert.That(result.TotalCostUsd, Is.EqualTo(0.01m));
            Assert.That(entries.Select(e => e.Role), Is.EqualTo(new[]
            {
                AgentChatRole.System, AgentChatRole.Assistant, AgentChatRole.System
            }));
        });
    }

    [Test]
    public async Task RunAsync_ExitWithoutResultEvent_FailsWithStderrTail()
    {
        var factory = new FakeProcessFactory("", exitCode: 1, stderr: "invalid api key");
        var agent = new ClaudeCodeCliAgent(Options(), factory);

        var result = await agent.RunAsync(Request, (_, _) => Task.CompletedTask);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("without a result event"));
            Assert.That(result.ErrorMessage, Does.Contain("invalid api key"));
        });
    }

    [Test]
    public async Task RunAsync_ErrorResult_FailsWithSubtype()
    {
        var factory = new FakeProcessFactory(
            """{"type":"result","subtype":"error_max_turns","is_error":true,"num_turns":7}""",
            exitCode: 0);
        var agent = new ClaudeCodeCliAgent(Options(), factory);

        var result = await agent.RunAsync(Request, (_, _) => Task.CompletedTask);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("error_max_turns"));
        });
    }

    [Test]
    public void RunAsync_Cancellation_KillsTheProcess()
    {
        var factory = new FakeProcessFactory("", exitCode: 0, blockOutput: true);
        var agent = new ClaudeCodeCliAgent(Options(), factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Assert.ThrowsAsync<TaskCanceledException>(() =>
            agent.RunAsync(Request, (_, _) => Task.CompletedTask, cts.Token));
        Assert.That(factory.LastProcess!.Killed, Is.True);
    }

    [Test]
    public void BuildStartInfo_InteractiveSession_UsesStreamJsonInput_AndStdioPermissions()
    {
        var agent = new ClaudeCodeCliAgent(Options());
        var interactive = Request with { Interaction = new FakeInteraction() };

        var startInfo = agent.BuildStartInfo(interactive);

        Assert.Multiple(() =>
        {
            Assert.That(startInfo.RedirectStandardInput, Is.True);
            Assert.That(startInfo.ArgumentList, Does.Contain("--input-format"));
            Assert.That(startInfo.ArgumentList, Does.Contain("--permission-prompt-tool"));
            Assert.That(startInfo.ArgumentList, Does.Not.Contain("--dangerously-skip-permissions"),
                "Interactive sessions route permissions to the operator instead of skipping them.");
            Assert.That(startInfo.ArgumentList, Does.Not.Contain(Request.Instruction),
                "The instruction travels as the first stream-json user message, not as an argument.");
        });
    }

    [Test]
    public async Task InteractiveSession_ControlRequest_IsAnsweredByTheOperator_OverStdin()
    {
        var stdout = """
            {"type":"system","subtype":"init","session_id":"s","model":"m","tools":[]}
            {"type":"control_request","request_id":"ctrl-1","request":{"subtype":"can_use_tool","tool_name":"Write","input":{"file_path":"a.md"}}}
            {"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"done"}
            """;
        var factory = new FakeProcessFactory(stdout, exitCode: 0);
        var interaction = new FakeInteraction(new AgentAnswer(["allow"]));
        var agent = new ClaudeCodeCliAgent(Options(), factory);

        var result = await agent.RunAsync(
            Request with { Interaction = interaction }, (_, _) => Task.CompletedTask);

        var stdin = factory.LastProcess!.Input.ToString()!;
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(interaction.LastQuestion?.Prompt, Does.Contain("Write"));
            Assert.That(stdin, Does.Contain(Request.Instruction),
                "The instruction is the first stream-json user message on stdin.");
            Assert.That(stdin, Does.Contain("control_response").And.Contain("\"behavior\":\"allow\""));
            Assert.That(stdin, Does.Contain("ctrl-1"));
            Assert.That(factory.LastProcess.InputClosed, Is.True,
                "The result event closes stdin so the CLI can exit.");
        });
    }

    [Test]
    public async Task InteractiveSession_DeniedControlRequest_SendsDenyWithTheOperatorsReason()
    {
        var stdout = """
            {"type":"control_request","request_id":"ctrl-2","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"rm -rf /"}}}
            {"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"done"}
            """;
        var factory = new FakeProcessFactory(stdout, exitCode: 0);
        var interaction = new FakeInteraction(new AgentAnswer(["deny"], "too dangerous"));
        var agent = new ClaudeCodeCliAgent(Options(), factory);

        await agent.RunAsync(Request with { Interaction = interaction }, (_, _) => Task.CompletedTask);

        var stdin = factory.LastProcess!.Input.ToString()!;
        Assert.That(stdin, Does.Contain("\"behavior\":\"deny\"").And.Contain("too dangerous"));
    }

    private sealed class FakeInteraction(AgentAnswer? answer = null) : IAgentInteraction
    {
        public AgentQuestion? LastQuestion { get; private set; }

        public Task<AgentAnswer> AskAsync(AgentQuestion question, CancellationToken cancellationToken)
        {
            LastQuestion = question;
            return Task.FromResult(answer ?? new AgentAnswer(["allow"]));
        }

        public async Task<string> WaitForGuidanceAsync(CancellationToken cancellationToken)
        {
            // No guidance in these tests - wait until the session tears the pump down.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class FakeProcessFactory(
        string stdout, int exitCode, string stderr = "", bool blockOutput = false) : IClaudeCliProcessFactory
    {
        public FakeProcess? LastProcess { get; private set; }

        public IClaudeCliProcess Start(ProcessStartInfo startInfo)
            => LastProcess = new FakeProcess(stdout, stderr, exitCode, blockOutput);
    }

    private sealed class FakeProcess(string stdout, string stderr, int exitCode, bool blockOutput)
        : IClaudeCliProcess
    {
        public bool Killed { get; private set; }

        public TextReader Output { get; } = blockOutput
            ? new BlockingReader()
            : new StringReader(stdout.ReplaceLineEndings("\n"));

        public TextReader Error { get; } = new StringReader(stderr);

        /// <summary>Captures everything the agent writes to the CLI's stdin.</summary>
        public StringWriter Input { get; } = new();

        TextWriter IClaudeCliProcess.Input => Input;

        public bool InputClosed { get; private set; }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult(exitCode);

        public void CloseInput() => InputClosed = true;

        public void Kill() => Killed = true;

        public void Dispose()
        {
        }

        /// <summary>Never yields a line — models a hanging CLI so cancellation is exercised.</summary>
        private sealed class BlockingReader : TextReader
        {
            public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
        }
    }
}

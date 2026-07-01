using System.Text.Json;
using Auxilia.ClaudeCode.Workflow;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Testing;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ClaudeCode.Workflow.Tests.ComponentTests;

/// <summary>
/// Drives the whole claude-code workflow through the SDK test harness (announcement →
/// directive → registration → slot configuration → run) with a scripted coding agent.
/// </summary>
[TestFixture, Category("Component")]
public sealed class ClaudeCodeWorkflowScenarioTests
{
    private string _outputDir = "";

    [SetUp]
    public void SetUp()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), $"cc-scenario-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("WORKFLOW_CONTEXT__TITLE", "Fix the flaky test");
        Environment.SetEnvironmentVariable("WORKFLOW_CONTEXT__BODY", "It fails every third run.");
        Environment.SetEnvironmentVariable(WorkflowEnvironmentVariables.OutputDirectory, _outputDir);
    }

    [TearDown]
    public void TearDown()
    {
        WorkflowBuilder.TestSlotHandlerResolver = null;
        Environment.SetEnvironmentVariable("WORKFLOW_CONTEXT__TITLE", null);
        Environment.SetEnvironmentVariable("WORKFLOW_CONTEXT__BODY", null);
        Environment.SetEnvironmentVariable(WorkflowEnvironmentVariables.OutputDirectory, null);
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }

    [Test]
    public async Task ScriptedAgentRun_ReachesSuccess_AndWritesTheSessionReport()
    {
        var agent = new ScriptedCodingAgent(succeed: true);
        RegisterAgent(agent);

        var result = await RunWorkflowAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
            Assert.That(agent.SeenInstruction,
                Is.EqualTo("Fix the flaky test\n\nIt fails every third run."),
                "The dispatch context must reach the agent as one instruction.");
        });

        var report = JsonSerializer.Deserialize<SessionReport>(
            await File.ReadAllTextAsync(Path.Combine(_outputDir, SessionReportWriter.FileName)))!;
        Assert.Multiple(() =>
        {
            Assert.That(report.Success, Is.True);
            Assert.That(report.Summary, Is.EqualTo("All fixed."));
            Assert.That(report.TurnCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task FailingAgentRun_ReachesFailed_ReportStillWritten()
    {
        RegisterAgent(new ScriptedCodingAgent(succeed: false));

        var result = await RunWorkflowAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(WorkflowState.Failed));
            Assert.That(result.ErrorMessage, Does.Contain("agent gave up"));
            Assert.That(File.Exists(Path.Combine(_outputDir, SessionReportWriter.FileName)), Is.True,
                "The report is the durable record — it must exist for failed runs too.");
        });
    }

    private static void RegisterAgent(ScriptedCodingAgent agent)
    {
        var resolver = new SlotHandlerResolver();
        resolver.Register("scripted-coding-agent", new ScriptedCodingAgentSlotHandler(agent));
        WorkflowBuilder.TestSlotHandlerResolver = resolver;
    }

    private static Task<HarnessResult> RunWorkflowAsync()
        => WorkflowTestHarness
            .For(() => ClaudeCodeWorkflow.RunAsync())
            .WithDirective(WorkflowDirectiveKind.Run)
            .WithTimeout(TimeSpan.FromSeconds(20))
            .WithSlot("coding-agent", "scripted-coding-agent", new Dictionary<string, string>())
            .Build()
            .RunAsync();

    private sealed class ScriptedCodingAgentSlotHandler(ScriptedCodingAgent agent) : ISlotHandler
    {
        public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
            => services.AddSingleton<ICodingAgent>(agent);
    }

    private sealed class ScriptedCodingAgent(bool succeed) : ICodingAgent
    {
        public string? SeenInstruction { get; private set; }

        public async Task<CodingAgentResult> RunAsync(
            CodingAgentRequest request,
            Func<AgentChatEntry, CancellationToken, Task> onChatEntry,
            CancellationToken cancellationToken = default)
        {
            SeenInstruction = request.Instruction;
            await onChatEntry(
                new AgentChatEntry(AgentChatRole.Assistant, "Taking a look.", DateTimeOffset.UtcNow),
                cancellationToken);
            return succeed
                ? new CodingAgentResult(true, "All fixed.", TurnCount: 2)
                : new CodingAgentResult(false, null, ErrorMessage: "agent gave up");
        }
    }
}

using System.Collections.Concurrent;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.SystemTestSuite.EndToEnd;

/// <summary>
/// The claude-code workflow end to end: a dispatched instruction launches the baked agent
/// image, the REAL claude-code-cli slot provider spawns the in-image stub CLI (cost rule:
/// never real AI in system tests), its stream-json transcript flows through the real parser
/// into the persisted "agent-conversation" chat view, and the session report is indexed as
/// an artifact.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class ClaudeCodeWorkflowSystemTests
{
    [Test]
    [CancelAfter(540_000)]
    public async Task DispatchedInstruction_RunsClaudeCodeAgent_StreamsChatAndPersistsReport(
        CancellationToken cancellationToken)
    {
        var bus = EndToEndEnvironment.MessageBusClient;

        var statusEvents = new ConcurrentQueue<WorkflowStatusEvent>();
        await bus.DeclareTopicExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await using var statusSubscription = await bus.SubscribeToTopicExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, ["#"],
            (msg, _) => { statusEvents.Enqueue(msg); return Task.CompletedTask; },
            cancellationToken);

        // 1. Dispatch an instruction through the Core Run API (the governed, tokenized path). The
        //    coding-agent slot is bound inline to the real claude-code-cli provider pointed at the
        //    in-image stub CLI (cost rule: never real AI in tests); the Core resolves it JIT.
        var runResp = await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            "/api/runs",
            new RunRequest(
                EndToEndEnvironment.ClaudeWorkflowType,
                new Dictionary<string, string>
                {
                    // Deliberately NO permission-mode: an unattended dispatch (no operator UI
                    // sent a choice) must default to auto-allow and never block on a
                    // permission card nobody answers.
                    ["Title"] = "Leave a note in the workspace",
                    ["Body"]  = "Create STUB_NOTES.md summarizing what you find."
                },
                SlotBindings: new List<SlotBinding>
                {
                    new("coding-agent", "claude-code-cli", Settings: new Dictionary<string, string>
                    {
                        ["ApiKey"]   = "e2e-stub-key",
                        ["CliPath"]  = EndToEndEnvironment.ClaudeStubCliPath,
                        ["MaxTurns"] = "5"
                    })
                }),
            cancellationToken);
        runResp.EnsureSuccessStatusCode();

        // 2. The run must complete successfully.
        Guid instanceId = default;
        var completed = await WaitForAsync(() =>
        {
            var complete = statusEvents
                .Where(e => e.WorkflowType == EndToEndEnvironment.ClaudeWorkflowType)
                .GroupBy(e => e.WorkflowInstanceId)
                .FirstOrDefault(g => g.Any(e => e.State == "Success"));
            if (complete is null)
                return false;
            instanceId = complete.Key;
            return true;
        }, TimeSpan.FromSeconds(210), cancellationToken);
        if (!completed)
            await FailWithDiagnosticsAsync(
                "The dispatched Claude Code run must reach Success.", statusEvents);

        await using var provider = EndToEndEnvironment.BuildPlatformDataProvider();

        // 3. The stub CLI's transcript must be persisted as the agent-chat view — parsed by
        //    the real stream-json parser, published live, replayable afterwards.
        var chatItems = (await provider.GetRequiredService<IDataAccess<ViewDataRecord>>()
                .ReadAsync(cancellationToken))
            .Where(v => v.WorkflowInstanceId == instanceId && v.ViewName == "agent-conversation")
            .OrderBy(v => v.Sequence)
            .Select(v => System.Text.Json.JsonSerializer.Deserialize<AgentChatEntry>(v.PayloadJson)!)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(chatItems.FirstOrDefault()?.Role, Is.EqualTo(AgentChatRole.User),
                "The instruction must open the conversation.");
            Assert.That(chatItems.FirstOrDefault()?.Content, Does.Contain("Leave a note in the workspace"));
            Assert.That(chatItems.Count(e => e.Role == AgentChatRole.Assistant),
                Is.GreaterThanOrEqualTo(2),
                "The stub transcript's assistant turns must be persisted.");
            Assert.That(chatItems.Where(e => e.Role == AgentChatRole.Tool && e.ToolState == "Running")
                    .Select(e => e.ToolName),
                Does.Contain("Bash").And.Contain("Write"),
                "tool_use events must surface as running tool entries.");
            Assert.That(chatItems.Count(e => e.Role == AgentChatRole.Tool && e.ToolState == "Success"),
                Is.GreaterThanOrEqualTo(2),
                "tool_result events must surface as completed tool entries.");
        });

        // 4. The session report must be indexed in the artifact store.
        var artifacts = await provider.GetRequiredService<IDataAccess<ArtifactRecord>>()
            .ReadAsync(cancellationToken);
        Assert.That(artifacts.Any(a =>
                a.RunInstanceId == instanceId && a.ArtifactType == "session-report"),
            Is.True, "The declared 'session-report' output must be indexed in the artifact store.");
    }

    private static async Task<bool> WaitForAsync(
        Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(500, ct);
        }
        return condition();
    }

    private static async Task FailWithDiagnosticsAsync(
        string message, ConcurrentQueue<WorkflowStatusEvent> statusEvents)
    {
        var observed = statusEvents.IsEmpty
            ? "  <none>"
            : string.Join("\n", statusEvents.Select(e =>
                $"  {e.TimestampUtc:HH:mm:ss} {e.WorkflowInstanceId} {e.WorkflowType} {e.State} {e.ErrorMessage}"));
        var siLogs = await EndToEndEnvironment.LogTailAsync(EndToEndEnvironment.Runner);
        Assert.Fail(
            $"{message}\n" +
            $"Observed status events:\n{observed}\n\n" +
            $"--- Runner logs (tail) ---\n{siLogs}");
    }
}

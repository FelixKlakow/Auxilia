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
/// The implementation workflow end to end: a dispatched run clones the per-run repository from
/// the authenticated git server (mount provider + connector), the REAL claude-code-cli slot
/// provider prepares the console (hook settings + fake account token) and the pipeline DRIVES
/// the in-image driven-stub CLI through tmux — refinement check, plan, implementation — with
/// turn completion arriving over the Claude Stop-hook contract. The story is the email
/// work-items slot's launch-context mail; AI review and the operator gates are disabled by run
/// inputs (no operator answers in a system test), push-mode=skip. Success must yield the
/// implementation-plan and review-bundle outputs and the persisted agent chat.
/// Cost rule: never real AI in system tests — the stub CLI stands in for the author.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class ImplementationWorkflowSystemTests
{
    private const string StoryId    = "story-e2e-1";
    private const string StoryTitle = "Add the implemented marker";

    [Test]
    [CancelAfter(540_000)]
    public async Task DispatchedStory_DrivenAuthorCompletesPipeline_PersistsPlanBundleAndChat(
        CancellationToken cancellationToken)
    {
        var bus = EndToEndEnvironment.MessageBusClient;

        var statusEvents = new ConcurrentQueue<WorkflowStatusEvent>();
        await bus.DeclareTopicExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await using var statusSubscription = await bus.SubscribeToTopicExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, ["#"],
            (msg, _) => { statusEvents.Enqueue(msg); return Task.CompletedTask; },
            cancellationToken);

        // 1. Register the generic git mount provider (role-tagged settings the runner's
        //    workspace materializer interprets) and the connector holding ONLY the git
        //    credential — the per-run repository pattern.
        var providerResp = await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            "/api/provider-catalog",
            new RegisterSlotProvider(
                ProviderType: "git-repository",
                Category: "workspace",
                Description: "A git repository materialized into the run's workspace.",
                Contracts: ["Auxilia.Workflows.SourceControl.ISourceControlAccess"],
                Settings:
                [
                    new RegisterProviderSetting("CloneUrl", "Repository", "Text", Required: true, Role: "clone-url"),
                    new RegisterProviderSetting("NoCache", "Fresh clone per run", "Boolean", Role: "no-cache"),
                    new RegisterProviderSetting("AllowPush", "Allow pushing", "Boolean", Role: "allow-push"),
                ],
                RequiredCredentialContract: "git-credential",
                MountsIntoWorkspace: true),
            cancellationToken);
        providerResp.EnsureSuccessStatusCode();

        var connectorResp = await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            "/api/connectors",
            new CreateConnector(
                Name: "impl-git-auth-" + Guid.NewGuid().ToString("N"),
                ProviderType: "azure-devops",
                Settings: new Dictionary<string, string>
                {
                    ["username"] = EndToEndEnvironment.GitUsername,
                    ["token"] = EndToEndEnvironment.GitPassword
                }),
            cancellationToken);
        connectorResp.EnsureSuccessStatusCode();
        var connector = (await connectorResp.Content.ReadFromJsonAsync<Connector>(cancellationToken))!;

        // 2. Dispatch the run. The context carries both the workflow's inputs (work-item-id +
        //    step toggles: no AI review, no operator gates, no push — nobody answers gates in a
        //    system test) and the email work-items slot's story fields (the same
        //    WORKFLOW_CONTEXT__* shape a mailbox trigger would deliver). The author binds the
        //    real claude-code-cli provider pointed at the in-image DRIVEN stub with a fake
        //    account token (never a real credential), so ClaudeInteractiveLogin writes the hook
        //    settings the stub signals turn completion through. The repository is a per-run
        //    clone from the authenticated git server, exposed to the workflow through the
        //    workspace slot provider pointing at the mount's announced root.
        var runResp = await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            "/api/runs",
            new RunRequest(
                EndToEndEnvironment.ImplementationWorkflowType,
                new Dictionary<string, string>
                {
                    ["work-item-id"] = StoryId,
                    ["refinement-check"] = "true",
                    ["ai-plan-review"] = "false",
                    ["ai-code-review"] = "false",
                    ["user-plan-gate"] = "false",
                    ["user-code-gate"] = "false",
                    ["push-mode"] = "skip",
                    ["gate-idle-compaction"] = "0",
                    // The email work-items slot reads the triggering mail from the launch context.
                    ["WorkItemId"] = StoryId,
                    ["Title"] = StoryTitle,
                    ["Body"] = "Append an 'implemented' marker to the repository.",
                    ["From"] = "operator@localhost"
                },
                SlotBindings: new List<SlotBinding>
                {
                    new("work-items", "email-work-items",
                        Settings: EndToEndEnvironment.EmailSettings()),
                    new("coding-agent", "claude-code-cli", Settings: new Dictionary<string, string>
                    {
                        ["OAuthToken"] = "e2e-fake-oauth-token",
                        ["CliPath"] = EndToEndEnvironment.DrivenStubCliPath
                    }),
                    // The per-run clone (mount) and the workspace-path slot exposing it.
                    new("workspace-repo", "git-repository", ConnectorId: connector.Id,
                        Settings: new Dictionary<string, string>
                        {
                            ["CloneUrl"] = EndToEndEnvironment.RepositoryCloneUrl,
                            ["NoCache"] = "true",
                            ["AllowPush"] = "true"
                        }),
                    new("repository", "coding-session-workspace", Settings: new Dictionary<string, string>
                    {
                        ["WorkingPath"] = "/workspace/repos/workspace-repo"
                    })
                }),
            cancellationToken);
        runResp.EnsureSuccessStatusCode();

        // 3. The run must reach Success — every driven turn completed over the hook contract.
        Guid instanceId = default;
        var completed = await WaitForAsync(() =>
        {
            var complete = statusEvents
                .Where(e => e.WorkflowType == EndToEndEnvironment.ImplementationWorkflowType)
                .GroupBy(e => e.WorkflowInstanceId)
                .FirstOrDefault(g => g.Any(e => e.State == "Success"));
            if (complete is null)
                return false;
            instanceId = complete.Key;
            return true;
        }, TimeSpan.FromSeconds(300), cancellationToken);
        if (!completed)
            await FailWithDiagnosticsAsync(
                "The dispatched implementation run must reach Success.", statusEvents);

        await using var provider = EndToEndEnvironment.BuildPlatformDataProvider();

        // 4. The declared outputs must be indexed: the plan, the review bundle (written
        //    unconditionally in the implementation loop), and the session report. Indexing
        //    trails the terminal status event by a moment — poll before asserting.
        var artifactStore = provider.GetRequiredService<IDataAccess<ArtifactRecord>>();
        var artifacts = new List<string>();
        await WaitForAsync(() =>
        {
            artifacts = artifactStore.ReadAsync(cancellationToken).GetAwaiter().GetResult()
                .Where(a => a.RunInstanceId == instanceId)
                .Select(a => a.ArtifactType)
                .ToList();
            return artifacts.Contains("implementation-plan")
                   && artifacts.Contains("review-bundle")
                   && artifacts.Contains("session-report");
        }, TimeSpan.FromSeconds(60), cancellationToken);
        Assert.Multiple(() =>
        {
            Assert.That(artifacts, Does.Contain("implementation-plan"),
                "The stub's plan.md must be indexed as the implementation-plan output.");
            Assert.That(artifacts, Does.Contain("review-bundle"),
                "The review bundle (status/plan/diff) must be indexed even without AI review.");
            Assert.That(artifacts, Does.Contain("session-report"),
                "The final session report must be indexed.");
        });

        // 5. The pipeline's chat view must be persisted: the story opens the conversation and
        //    the plan is published as an assistant entry. Persistence trails the live stream —
        //    poll like the artifact index above.
        var viewStore = provider.GetRequiredService<IDataAccess<ViewDataRecord>>();
        var chatItems = new List<AgentChatEntry>();
        await WaitForAsync(() =>
        {
            chatItems = viewStore.ReadAsync(cancellationToken).GetAwaiter().GetResult()
                .Where(v => v.WorkflowInstanceId == instanceId && v.ViewName == "agent-conversation")
                .OrderBy(v => v.Sequence)
                .Select(v => System.Text.Json.JsonSerializer.Deserialize<AgentChatEntry>(v.PayloadJson)!)
                .ToList();
            return chatItems.Any(e => e.Role == AgentChatRole.Assistant);
        }, TimeSpan.FromSeconds(60), cancellationToken);
        Assert.Multiple(() =>
        {
            Assert.That(chatItems.FirstOrDefault()?.Role, Is.EqualTo(AgentChatRole.User),
                "The user story must open the conversation.");
            Assert.That(chatItems.FirstOrDefault()?.Content, Does.Contain(StoryTitle));
            Assert.That(chatItems.Where(e => e.Role == AgentChatRole.Assistant)
                    .Select(e => e.Content),
                Has.Some.Contains("Implementation plan"),
                "The stub's plan must be persisted as an assistant chat entry.");
        });
    }

    /// <summary>
    /// The SIMULATION path end to end — what the steering client's "[SIM]" configuration runs: the
    /// scripted work-item source (TFS stand-in) supplies the story AND a state vocabulary, so
    /// the finalization stage raises the story-state gate; the test answers it over the raw
    /// steering wire (form-answer via deliver-input) like the steering client would. Asserts the
    /// declared flow's runtime states walk workspace → refinement → plan → implement →
    /// finalization in order and end all-done.
    /// </summary>
    [Test]
    [CancelAfter(540_000)]
    public async Task SimulatedStory_StateGateAnswered_WalksTheDeclaredFlow(
        CancellationToken cancellationToken)
    {
        var bus = EndToEndEnvironment.MessageBusClient;

        var statusEvents = new ConcurrentQueue<WorkflowStatusEvent>();
        await bus.DeclareTopicExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await using var statusSubscription = await bus.SubscribeToTopicExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, ["#"],
            (msg, _) => { statusEvents.Enqueue(msg); return Task.CompletedTask; },
            cancellationToken);

        var flowSnapshots = new ConcurrentQueue<(Guid Instance, string Payload)>();
        var stateGates = new ConcurrentQueue<(Guid Instance, string RequestId)>();
        await bus.DeclareTopicExchangeAsync(ViewDataMessage.ExchangeName, cancellationToken);
        await using var viewSubscription = await bus.SubscribeToTopicExchangeAsync<ViewDataMessage>(
            ViewDataMessage.ExchangeName, ["#"],
            (msg, _) =>
            {
                if (msg.ViewName == "flow")
                    flowSnapshots.Enqueue((msg.WorkflowInstanceId, msg.PayloadJson));
                if (msg.ViewName == "steering"
                    && msg.PayloadJson.Contains("form-requested")
                    && msg.PayloadJson.Contains("state-gate"))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(msg.PayloadJson);
                    if (doc.RootElement.TryGetProperty("requestId", out var rid)
                        && rid.GetString() is { Length: > 0 } requestId)
                        stateGates.Enqueue((msg.WorkflowInstanceId, requestId));
                }
                return Task.CompletedTask;
            },
            cancellationToken);

        // The git mount provider/credential — registered by the sibling test when it ran
        // first; a duplicate registration is fine to ignore.
        await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            "/api/provider-catalog",
            new RegisterSlotProvider(
                ProviderType: "git-repository",
                Category: "workspace",
                Description: "A git repository materialized into the run's workspace.",
                Contracts: ["Auxilia.Workflows.SourceControl.ISourceControlAccess"],
                Settings:
                [
                    new RegisterProviderSetting("CloneUrl", "Repository", "Text", Required: true, Role: "clone-url"),
                    new RegisterProviderSetting("NoCache", "Fresh clone per run", "Boolean", Role: "no-cache"),
                    new RegisterProviderSetting("AllowPush", "Allow pushing", "Boolean", Role: "allow-push"),
                ],
                RequiredCredentialContract: "git-credential",
                MountsIntoWorkspace: true),
            cancellationToken);
        var connectorResp = await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            "/api/connectors",
            new CreateConnector(
                Name: "sim-git-auth-" + Guid.NewGuid().ToString("N"),
                ProviderType: "azure-devops",
                Settings: new Dictionary<string, string>
                {
                    ["username"] = EndToEndEnvironment.GitUsername,
                    ["token"] = EndToEndEnvironment.GitPassword
                }),
            cancellationToken);
        connectorResp.EnsureSuccessStatusCode();
        var connector = (await connectorResp.Content.ReadFromJsonAsync<Connector>(cancellationToken))!;

        var runResp = await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            "/api/runs",
            new RunRequest(
                EndToEndEnvironment.ImplementationWorkflowType,
                new Dictionary<string, string>
                {
                    ["work-item-id"] = "SIM-42",
                    ["refinement-check"] = "true",
                    ["ai-plan-review"] = "false",
                    ["ai-code-review"] = "false",
                    ["user-plan-gate"] = "false",
                    ["user-code-gate"] = "false",
                    ["push-mode"] = "skip",
                    ["gate-idle-compaction"] = "0"
                },
                SlotBindings: new List<SlotBinding>
                {
                    new("work-items", "simulated-work-items", Settings: new Dictionary<string, string>
                    {
                        ["Title"] = "Simulated story: add the implemented marker",
                        ["DelaySeconds"] = "0"
                    }),
                    new("coding-agent", "claude-code-cli", Settings: new Dictionary<string, string>
                    {
                        ["OAuthToken"] = "e2e-fake-oauth-token",
                        ["CliPath"] = EndToEndEnvironment.DrivenStubCliPath
                    }),
                    new("workspace-repo", "git-repository", ConnectorId: connector.Id,
                        Settings: new Dictionary<string, string>
                        {
                            ["CloneUrl"] = EndToEndEnvironment.RepositoryCloneUrl,
                            ["NoCache"] = "true",
                            ["AllowPush"] = "true"
                        }),
                    new("repository", "coding-session-workspace", Settings: new Dictionary<string, string>
                    {
                        ["WorkingPath"] = "/workspace/repos/workspace-repo"
                    })
                }),
            cancellationToken);
        runResp.EnsureSuccessStatusCode();

        // The finalization stage must raise the story-state gate (the sim source HAS states).
        var gateSeen = await WaitForAsync(() => !stateGates.IsEmpty,
            TimeSpan.FromSeconds(240), cancellationToken);
        if (!gateSeen)
            await FailWithDiagnosticsAsync("The story-state gate never arrived.", statusEvents);
        stateGates.TryDequeue(out var gate);

        // Answer it over the wire exactly like the steering client: form-answer via deliver-input.
        var answer = await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            $"/api/runs/{gate.Instance}/inputs",
            new ProvideRunInput(
                """{"$type":"form-answer","requestId":"__RID__","answers":[{"questionId":"state-gate","selectedIds":["Resolved"]}]}"""
                    .Replace("__RID__", gate.RequestId)),
            cancellationToken);
        answer.EnsureSuccessStatusCode();

        var completed = await WaitForAsync(() => statusEvents.Any(
                e => e.WorkflowInstanceId == gate.Instance && e.State == "Success"),
            TimeSpan.FromSeconds(120), cancellationToken);
        if (!completed)
            await FailWithDiagnosticsAsync(
                "The simulated run must reach Success after the state gate is answered.", statusEvents);

        // The declared flow's runtime states must have walked the stages in order and end done.
        var snapshots = flowSnapshots.Where(f => f.Instance == gate.Instance)
            .Select(f => ParseFlow(f.Payload)).ToList();
        var activeSequence = snapshots
            .Select(steps => steps.FirstOrDefault(s => s.State == "active").Id)
            .Where(id => id is not null)
            .Distinct()
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(activeSequence, Is.EqualTo(new[]
                    { "workspace", "refinement", "plan", "implement", "finalization" }),
                "The stages must activate in declared order.");
            Assert.That(snapshots.Last().Select(s => s.State), Is.All.EqualTo("done"),
                "The final snapshot marks every stage done.");
        });
    }

    private static List<(string Id, string State)> ParseFlow(string payloadJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
        return doc.RootElement.GetProperty("Steps").EnumerateArray()
            .Select(s => (s.GetProperty("Id").GetString()!, s.GetProperty("State").GetString()!))
            .ToList();
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
        var runnerLogs = await EndToEndEnvironment.LogTailAsync(EndToEndEnvironment.Runner);
        Assert.Fail(
            $"{message}\n" +
            $"Observed status events:\n{observed}\n\n" +
            $"--- Runner logs (tail) ---\n{runnerLogs}");
    }
}

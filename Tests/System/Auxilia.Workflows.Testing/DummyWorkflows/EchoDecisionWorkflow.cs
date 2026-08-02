using System.Text.Json;
using System.Text.Json.Serialization;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Dummy workflow exercising the full steer loop over the wire: it proposes a random action as a
/// steering-protocol <c>action-proposed</c> item on its <c>steering</c> view, BLOCKS awaiting the
/// decision via <see cref="Auxilia.Workflows.IWorkflowInputs"/> (delivered through the Core's
/// deliver-input endpoint), then echoes the decision into its <c>output</c> view and ends the
/// session. It deliberately does NOT reference the steering client's protocol library — it emits/parses
/// the raw wire JSON, which is precisely the cross-implementation protocol test.
/// </summary>
public static class EchoDecisionWorkflow
{
    public const string WorkflowName = "echo-decision-workflow";
    public const string SteeringViewName = "steering";
    public const string OutputViewName = "output";

    // Wire shapes of the steering protocol (discriminator "$type" must be the FIRST property).
    private sealed record ProposalWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("actionId")] string ActionId,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("payloadJson")] string? PayloadJson);

    private sealed record SessionEndedWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record CapabilitiesWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("accepts")] IReadOnlyList<string> Accepts);

    private sealed record ActionResolvedWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("actionId")] string ActionId);

    private static readonly (string Kind, string Summary)[] SampleActions =
    [
        ("deploy", "Deploy build 42 to the production cluster"),
        ("delete-branch", "Delete the stale branch feature/legacy-cleanup"),
        ("send-mail", "Send the release announcement to the whole team"),
        ("scale-up", "Scale the worker pool from 3 to 12 instances"),
    ];

    public static Task RunAsync(string[] args) =>
        WorkflowBuilder
            .Create(WorkflowName)
            .DeclaresView<ProposalWire>(SteeringViewName, ViewRendering.Custom, ViewLifecycle.LiveAndPersisted)
            .DeclaresView<EchoWorkflow.EchoLine>(OutputViewName, ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            .DeclaresTrigger(TriggerDeclaration.Manual, "Run and decide the proposed action from the steering client.")
            .WithApplication(ExecuteAsync)
            .Run(args);

    private static async Task ExecuteAsync(IServiceProvider provider, CancellationToken ct)
    {
        var views = provider.GetRequiredService<IViewPublisher>();
        var inputs = provider.GetRequiredService<IWorkflowInputs>();

        // Announce the steering surface first: only announced hub-command kinds are offered
        // by the steering client (no handler here → no control there).
        await views.PublishAsync(SteeringViewName,
            new CapabilitiesWire("capabilities", ["guidance", "action-decision", "halt"]), ct);

        var (kind, summary) = SampleActions[Random.Shared.Next(SampleActions.Length)];
        var actionId = Guid.NewGuid().ToString("N");

        await Echo(views, $"proposing action '{kind}' — awaiting the operator's decision …", ct);
        await views.PublishAsync(SteeringViewName,
            new ProposalWire("action-proposed", actionId, kind, summary, null), ct);

        // The hold: block until the matching decision arrives (max 10 minutes so an undecided
        // run does not live forever). Guidance received while holding is echoed, not consumed.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        while (true)
        {
            string payload;
            try
            {
                payload = await inputs.ReceiveAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await Echo(views, "no decision arrived within 10 minutes — giving up.", ct);
                await views.PublishAsync(SteeringViewName,
                    new SessionEndedWire("session-ended", false, "decision timeout"), ct);
                return;
            }

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var type = root.TryGetProperty("$type", out var t) ? t.GetString() : null;

            if (type == "guidance")
            {
                var text = root.TryGetProperty("text", out var g) ? g.GetString() : null;
                await Echo(views, $"guidance received: {text}", ct);
                continue;
            }

            if (type == "halt")
            {
                await Echo(views, "halt received — stopping.", ct);
                await views.PublishAsync(SteeringViewName,
                    new SessionEndedWire("session-ended", false, "halted by the operator"), ct);
                return;
            }

            if (type == "action-decision")
            {
                var decidedId = root.TryGetProperty("actionId", out var a) ? a.GetString() : null;
                if (!string.Equals(decidedId, actionId, StringComparison.Ordinal))
                {
                    await Echo(views, $"ignoring a decision for unknown action '{decidedId}'.", ct);
                    continue;
                }

                // DecisionOutcome rides as a number with web defaults (0 = Approve); accept a
                // string spelling too, so both codec styles interoperate.
                var approved = root.TryGetProperty("outcome", out var o) && o.ValueKind switch
                {
                    JsonValueKind.Number => o.GetInt32() == 0,
                    JsonValueKind.String => string.Equals(o.GetString(), "approve", StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };
                var rationale = root.TryGetProperty("rationale", out var r) ? r.GetString() : null;

                // The decision is consumed — tell observers so the pending card never resurfaces.
                await views.PublishAsync(SteeringViewName,
                    new ActionResolvedWire("action-resolved", actionId), ct);
                await Echo(views,
                    $"decision received: action '{kind}' was {(approved ? "APPROVED" : "REJECTED")}"
                    + (string.IsNullOrWhiteSpace(rationale) ? "" : $" — \"{rationale}\""), ct);
                await Echo(views, approved
                    ? $"executing '{summary}' … done."
                    : "nothing executed.", ct);
                await views.PublishAsync(SteeringViewName,
                    new SessionEndedWire("session-ended", true, null), ct);
                return;
            }

            await Echo(views, $"unrecognized input ({type ?? "no $type"}) — ignored.", ct);
        }
    }

    private static Task Echo(IViewPublisher views, string line, CancellationToken ct)
    {
        Console.WriteLine($"[EchoDecisionWorkflow] {line}");
        return views.PublishAsync(OutputViewName, new EchoWorkflow.EchoLine(line), ct);
    }
}

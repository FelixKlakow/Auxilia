using System.Text.Json;
using System.Text.Json.Serialization;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Dummy workflow exercising the multi-question decision FORM over the wire (Claude-style): one
/// <c>form-requested</c> carrying a single-select question (with a free-text "other"), a
/// multi-select question, and a pure free-text question — all answered together with one submit,
/// blocking via <see cref="Auxilia.Workflows.IWorkflowInputs"/>, then echoed. Like its siblings it
/// emits/parses the raw wire JSON without the steering client's protocol library — the
/// cross-implementation protocol test.
/// </summary>
public static class EchoChoiceWorkflow
{
    public const string WorkflowName = "echo-choice-workflow";
    public const string SteeringViewName = "steering";
    public const string OutputViewName = "output";

    private sealed record OptionWire(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("description")] string? Description);

    private sealed record QuestionWire(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("options")] IReadOnlyList<OptionWire> Options,
        [property: JsonPropertyName("multiSelect")] bool MultiSelect,
        [property: JsonPropertyName("allowFreeText")] bool AllowFreeText);

    private sealed record FormWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("questions")] IReadOnlyList<QuestionWire> Questions);

    private sealed record FormResolvedWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("requestId")] string RequestId);

    private sealed record SessionEndedWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record CapabilitiesWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("accepts")] IReadOnlyList<string> Accepts);

    public static Task RunAsync(string[] args) =>
        WorkflowBuilder
            .Create(WorkflowName)
            .DeclaresView<FormWire>(SteeringViewName, ViewRendering.Custom, ViewLifecycle.LiveAndPersisted)
            .DeclaresView<EchoWorkflow.EchoLine>(OutputViewName, ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            .DeclaresTrigger(TriggerDeclaration.Manual, "Run and answer the decision form from the steering client.")
            .WithApplication(ExecuteAsync)
            .Run(args);

    private static async Task ExecuteAsync(IServiceProvider provider, CancellationToken ct)
    {
        var views = provider.GetRequiredService<IViewPublisher>();
        var inputs = provider.GetRequiredService<Auxilia.Workflows.IWorkflowInputs>();

        // Announce the steering surface first: only announced hub-command kinds are offered
        // by the steering client (no handler here → no control there).
        await views.PublishAsync(SteeringViewName,
            new CapabilitiesWire("capabilities", ["guidance", "form-answer", "halt"]), ct);

        var requestId = Guid.NewGuid().ToString("N");
        await Echo(views, "asking the release form (3 questions, one submit) …", ct);
        await views.PublishAsync(SteeringViewName, new FormWire("form-requested", requestId,
        [
            new QuestionWire("environment", "Which environment should this release go to?",
            [
                new OptionWire("staging", "Staging", "The pre-production mirror"),
                new OptionWire("production", "Production", "The real thing"),
                new OptionWire("canary", "Canary", "5% of production traffic"),
            ], MultiSelect: false, AllowFreeText: true),
            new QuestionWire("services", "Which services should be restarted afterwards?",
            [
                new OptionWire("core-api", "Core API", null),
                new OptionWire("core-runner", "Core Runner", null),
                new OptionWire("workflow-library", "Workflow Library", null),
                new OptionWire("admin-console", "Admin Console", null),
            ], MultiSelect: true, AllowFreeText: false),
            new QuestionWire("notes", "Anything to put into the release notes?",
                [], MultiSelect: false, AllowFreeText: true),
        ]), ct);

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
                await Echo(views, "no answers arrived within 10 minutes — giving up.", ct);
                await views.PublishAsync(SteeringViewName,
                    new SessionEndedWire("session-ended", false, "form timeout"), ct);
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

            if (type == "form-answer")
            {
                var answeredId = root.TryGetProperty("requestId", out var r) ? r.GetString() : null;
                if (!string.Equals(answeredId, requestId, StringComparison.Ordinal))
                {
                    await Echo(views, $"ignoring answers for unknown request '{answeredId}'.", ct);
                    continue;
                }

                await views.PublishAsync(SteeringViewName,
                    new FormResolvedWire("form-resolved", requestId), ct);

                if (root.TryGetProperty("answers", out var answers) && answers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var answer in answers.EnumerateArray())
                    {
                        var questionId = answer.TryGetProperty("questionId", out var q) ? q.GetString() : "?";
                        var selected = new List<string>();
                        if (answer.TryGetProperty("selectedIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                            selected.AddRange(ids.EnumerateArray()
                                .Where(e => e.ValueKind == JsonValueKind.String)
                                .Select(e => e.GetString()!));
                        var freeText = answer.TryGetProperty("freeText", out var f) ? f.GetString() : null;

                        var rendered = selected.Count > 0 ? string.Join(", ", selected) : "(none)";
                        if (!string.IsNullOrWhiteSpace(freeText))
                            rendered += $" — \"{freeText}\"";
                        await Echo(views, $"{questionId}: {rendered}", ct);
                    }
                }

                await Echo(views, "form received — done.", ct);
                await views.PublishAsync(SteeringViewName,
                    new SessionEndedWire("session-ended", true, null), ct);
                return;
            }

            await Echo(views, $"unrecognized input ({type ?? "no $type"}) — ignored.", ct);
        }
    }

    private static Task Echo(IViewPublisher views, string line, CancellationToken ct)
    {
        Console.WriteLine($"[EchoChoiceWorkflow] {line}");
        return views.PublishAsync(OutputViewName, new EchoWorkflow.EchoLine(line), ct);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Auxilia.Workflows.Views;

namespace Auxilia.Workflows.Steering;

/// <summary>
/// One question a workflow asks its operator through the steering client. <see cref="Detail"/> is an
/// optional preformatted block (e.g. the tool input of a permission request) rendered monospace
/// under the prompt.
/// </summary>
public sealed record OperatorQuestion(
    string Id,
    string Prompt,
    IReadOnlyList<OperatorOption> Options,
    bool MultiSelect = false,
    bool AllowFreeText = false,
    string? Detail = null);

/// <summary>One selectable option of an <see cref="OperatorQuestion"/>.</summary>
public sealed record OperatorOption(string Id, string Label, string? Description = null);

/// <summary>The operator's answer to one question: selected option ids and/or free text.</summary>
public sealed record OperatorAnswer(string QuestionId, IReadOnlyList<string> SelectedIds, string? FreeText);

/// <summary>
/// A live session-setting change from the operator (e.g. permission mode, model). Keys are the
/// consuming workflow's vocabulary — the channel carries them verbatim.
/// </summary>
public sealed record OperatorSetting(string Key, string Value);

/// <summary>
/// The workflow side of the operator steering loop, speaking the steering wire protocol over the
/// run's <c>steering</c> view (out) and the Core's deliver-input channel (in):
/// <list type="bullet">
/// <item><see cref="AskAsync"/> raises a question form in the steering client and blocks until answered.</item>
/// <item><see cref="WaitForGuidanceAsync"/> yields guidance texts the operator sends.</item>
/// <item><see cref="HaltToken"/> cancels when the operator halts the run.</item>
/// </list>
/// Start announces the capabilities; <see cref="EndSessionAsync"/> tells observers the session is
/// over so no stale decision cards survive. The wire shapes are protocol JSON — the platform
/// carries them verbatim and never interprets them.
/// </summary>
public sealed class OperatorChannel : IAsyncDisposable
{
    public const string ViewName = "steering";

    private readonly IViewPublisher _views;
    private readonly IAsyncDisposable _pump;
    private readonly CancellationTokenSource _halt = new();
    private readonly CancellationTokenSource _end = new();
    private readonly Channel<string> _guidance = Channel.CreateUnbounded<string>();
    private readonly Channel<OperatorSetting> _settings = Channel.CreateUnbounded<OperatorSetting>();
    private readonly Dictionary<string, TaskCompletionSource<IReadOnlyList<OperatorAnswer>>> _pendingForms = [];
    private readonly Lock _gate = new();

    private OperatorChannel(IViewPublisher views, IWorkflowInputs inputs, CancellationToken lifetime)
    {
        _views = views;
        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var pumpTask = PumpAsync(inputs, pumpCts.Token);
        _pump = new Pump(pumpCts, pumpTask);
    }

    /// <summary>
    /// Opens the channel and announces the run's steering capabilities to observers.
    /// <paramref name="extraCapabilities"/> extends the baseline set (e.g. "end" for sessions
    /// the operator can finish gracefully).
    /// </summary>
    public static async Task<OperatorChannel> StartAsync(
        IViewPublisher views, IWorkflowInputs inputs, CancellationToken lifetime = default,
        IReadOnlyList<string>? extraCapabilities = null)
    {
        var channel = new OperatorChannel(views, inputs, lifetime);
        string[] baseline = ["guidance", "form-answer", "halt", "setting"];
        await views.PublishAsync(ViewName,
            new CapabilitiesWire("capabilities", [.. baseline, .. extraCapabilities ?? []]), lifetime);
        return channel;
    }

    /// <summary>Cancelled when the operator halts the run from the steering client.</summary>
    public CancellationToken HaltToken => _halt.Token;

    /// <summary>Cancelled when the operator gracefully ends the session (multi-turn runs).</summary>
    public CancellationToken EndToken => _end.Token;

    /// <summary>Raises a question form in the steering client and blocks until the operator submits it.</summary>
    public async Task<IReadOnlyList<OperatorAnswer>> AskAsync(
        IReadOnlyList<OperatorQuestion> questions, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<IReadOnlyList<OperatorAnswer>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _pendingForms[requestId] = tcs;

        await _views.PublishAsync(ViewName, new FormRequestedWire(
            "form-requested", requestId,
            questions.Select(q => new FormQuestionWire(
                q.Id, q.Prompt,
                q.Options.Select(o => new OptionWire(o.Id, o.Label, o.Description)).ToList(),
                q.MultiSelect, q.AllowFreeText, q.Detail)).ToList()), cancellationToken);

        try
        {
            using var abort = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            var answers = await tcs.Task;
            await _views.PublishAsync(ViewName, new FormResolvedWire("form-resolved", requestId), cancellationToken);
            return answers;
        }
        finally
        {
            lock (_gate)
                _pendingForms.Remove(requestId);
        }
    }

    /// <summary>Waits for the next guidance text from the operator.</summary>
    public ValueTask<string> WaitForGuidanceAsync(CancellationToken cancellationToken)
        => _guidance.Reader.ReadAsync(cancellationToken);

    /// <summary>Waits for the next live session-setting change from the operator.</summary>
    public ValueTask<OperatorSetting> WaitForSettingAsync(CancellationToken cancellationToken)
        => _settings.Reader.ReadAsync(cancellationToken);

    /// <summary>Announces the end of the session — observers drop every pending decision card.</summary>
    public Task EndSessionAsync(bool success, string? error, CancellationToken cancellationToken)
        => _views.PublishAsync(ViewName, new SessionEndedWire("session-ended", success, error), cancellationToken);

    /// <summary>
    /// Announces that one agent turn finished and the session is idle awaiting operator input —
    /// the steering client's cue to notify the user.
    /// </summary>
    public Task PublishTurnEndedAsync(int turn, string? summary, CancellationToken cancellationToken)
        => _views.PublishAsync(ViewName, new TurnEndedWire("turn-ended", turn, summary), cancellationToken);

    /// <summary>
    /// Announces the session's live vocabulary — switchable models with their reasoning
    /// efforts — so steering client dropdowns are never statically populated.
    /// </summary>
    public Task PublishSessionVocabularyAsync(
        IReadOnlyList<(string Id, string Label, IReadOnlyList<string> Efforts, string? DefaultEffort)> models,
        CancellationToken cancellationToken)
        => _views.PublishAsync(ViewName, new SessionVocabularyWire(
            "session-vocabulary",
            models.Select(m => new VocabularyModelWire(m.Id, m.Label, m.Efforts, m.DefaultEffort)).ToList()),
            cancellationToken);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();

    private async Task PumpAsync(IWorkflowInputs inputs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string payload;
            try
            {
                payload = await inputs.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                Dispatch(payload);
            }
            catch (JsonException)
            {
                // Unparseable operator input is dropped — the protocol is additive, never fatal.
            }
        }
    }

    private void Dispatch(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        switch (root.TryGetProperty("$type", out var t) ? t.GetString() : null)
        {
            case "guidance":
                if (root.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } guidance)
                    _guidance.Writer.TryWrite(guidance);
                break;

            case "halt":
                _halt.Cancel();
                break;

            case "end":
                _end.Cancel();
                break;

            case "setting":
                if (root.TryGetProperty("key", out var key) && key.GetString() is { Length: > 0 } settingKey
                    && root.TryGetProperty("value", out var v) && v.GetString() is { } settingValue)
                    _settings.Writer.TryWrite(new OperatorSetting(settingKey, settingValue));
                break;

            case "form-answer":
                var requestId = root.TryGetProperty("requestId", out var id) ? id.GetString() : null;
                if (requestId is null)
                    break;
                TaskCompletionSource<IReadOnlyList<OperatorAnswer>>? tcs;
                lock (_gate)
                    _pendingForms.TryGetValue(requestId, out tcs);
                tcs?.TrySetResult(ParseAnswers(root));
                break;
        }
    }

    private static List<OperatorAnswer> ParseAnswers(JsonElement root)
    {
        var answers = new List<OperatorAnswer>();
        if (!root.TryGetProperty("answers", out var array) || array.ValueKind != JsonValueKind.Array)
            return answers;
        foreach (var answer in array.EnumerateArray())
        {
            var questionId = answer.TryGetProperty("questionId", out var q) ? q.GetString() : null;
            if (questionId is null)
                continue;
            var selected = new List<string>();
            if (answer.TryGetProperty("selectedIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                selected.AddRange(ids.EnumerateArray()
                    .Select(i => i.GetString())
                    .Where(i => i is { Length: > 0 })!);
            var freeText = answer.TryGetProperty("freeText", out var f) ? f.GetString() : null;
            answers.Add(new OperatorAnswer(questionId, selected, freeText));
        }
        return answers;
    }

    private sealed record Pump(CancellationTokenSource Cts, Task Task) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Cts.CancelAsync();
            try
            {
                await Task;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            Cts.Dispose();
        }
    }

    // Wire shapes of the steering protocol (discriminator "$type" is the first property).
    private sealed record CapabilitiesWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("accepts")] IReadOnlyList<string> Accepts);

    private sealed record OptionWire(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("description")] string? Description);

    private sealed record FormQuestionWire(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("options")] IReadOnlyList<OptionWire> Options,
        [property: JsonPropertyName("multiSelect")] bool MultiSelect,
        [property: JsonPropertyName("allowFreeText")] bool AllowFreeText,
        [property: JsonPropertyName("detail")] string? Detail);

    private sealed record FormRequestedWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("questions")] IReadOnlyList<FormQuestionWire> Questions);

    private sealed record FormResolvedWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("requestId")] string RequestId);

    private sealed record SessionEndedWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record TurnEndedWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("turn")] int Turn,
        [property: JsonPropertyName("summary")] string? Summary);

    private sealed record VocabularyModelWire(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("efforts")] IReadOnlyList<string> Efforts,
        [property: JsonPropertyName("defaultEffort")] string? DefaultEffort);

    private sealed record SessionVocabularyWire(
        [property: JsonPropertyName("$type")] string Type,
        [property: JsonPropertyName("models")] IReadOnlyList<VocabularyModelWire> Models);
}

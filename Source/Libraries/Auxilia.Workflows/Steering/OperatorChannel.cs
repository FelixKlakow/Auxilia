using System.Threading.Channels;
using Auxilia.Steering.Codec;
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
    string? Detail = null)
{
    /// <summary>
    /// How clients render <see cref="Detail"/> — open vocabulary: null/"code" = monospace,
    /// "markdown" = rendered markdown (plans, review bundles).
    /// </summary>
    public string? DetailFormat { get; init; }
}

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
        string[] baseline = [
            SteeringGuidance.TypeName, SteeringFormAnswer.TypeName,
            SteeringHalt.TypeName, SteeringSetting.TypeName];
        await views.PublishAsync(ViewName,
            new SteeringCapabilities([.. baseline, .. extraCapabilities ?? []]), lifetime);
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

        await _views.PublishAsync(ViewName, new SteeringFormRequested(
            requestId,
            questions.Select(q => new SteeringFormQuestion(
                q.Id, q.Prompt,
                q.Options.Select(o => new SteeringFormOption(o.Id, o.Label, o.Description)).ToList(),
                q.MultiSelect, q.AllowFreeText, q.Detail, q.DetailFormat)).ToList()), cancellationToken);

        try
        {
            using var abort = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            var answers = await tcs.Task;
            await _views.PublishAsync(ViewName, new SteeringFormResolved(requestId), cancellationToken);
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
        => _views.PublishAsync(ViewName, new SteeringSessionEnded(success, error), cancellationToken);

    /// <summary>
    /// Announces that one agent turn finished and the session is idle awaiting operator input —
    /// the steering client's cue to notify the user.
    /// </summary>
    public Task PublishTurnEndedAsync(int turn, string? summary, CancellationToken cancellationToken)
        => _views.PublishAsync(ViewName, new SteeringTurnEnded(turn, summary), cancellationToken);

    /// <summary>
    /// Announces the session's live vocabulary — switchable models with their reasoning
    /// efforts — so steering client dropdowns are never statically populated.
    /// </summary>
    public Task PublishSessionVocabularyAsync(
        IReadOnlyList<(string Id, string Label, IReadOnlyList<string> Efforts, string? DefaultEffort)> models,
        CancellationToken cancellationToken)
        => _views.PublishAsync(ViewName, new SteeringSessionVocabulary(
            models.Select(m => new SteeringVocabularyModel(m.Id, m.Label, m.Efforts, m.DefaultEffort)).ToList()),
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

            Dispatch(payload);
        }
    }

    private void Dispatch(string payload)
    {
        // Unparseable or unknown operator input decodes to null and is dropped — the
        // protocol is additive, never fatal.
        switch (SteeringCodec.Decode(payload))
        {
            case SteeringGuidance { Text.Length: > 0 } guidance:
                _guidance.Writer.TryWrite(guidance.Text);
                break;

            case SteeringHalt:
                _halt.Cancel();
                break;

            case SteeringEnd:
                _end.Cancel();
                break;

            case SteeringSetting { Key.Length: > 0 } setting:
                _settings.Writer.TryWrite(new OperatorSetting(setting.Key, setting.Value));
                break;

            case SteeringFormAnswer formAnswer:
                TaskCompletionSource<IReadOnlyList<OperatorAnswer>>? tcs;
                lock (_gate)
                    _pendingForms.TryGetValue(formAnswer.RequestId, out tcs);
                tcs?.TrySetResult(formAnswer.Answers
                    .Select(a => new OperatorAnswer(a.QuestionId, a.SelectedIds, a.FreeText))
                    .ToList());
                break;
        }
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
}

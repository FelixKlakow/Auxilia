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
/// How long a turn-based session may wait for its operator before it ends by itself, as if the
/// operator had sent <c>end</c> (reason <see cref="SessionEndReasons.IdleTimeout"/>). The session
/// waits after a turn ended (<see cref="OperatorChannel.PublishTurnEndedAsync"/>), from its start
/// when it has no first instruction (<paramref name="AwaitingFirstTurn"/>), and while a question
/// to the operator is open; any input from the operator restarts the clock.
/// </summary>
public sealed record SessionIdlePolicy(TimeSpan Timeout, bool AwaitingFirstTurn = false, TimeProvider? Time = null);

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
/// <para>
/// With a <see cref="SessionIdlePolicy"/> the channel also ends a turn-based session nobody
/// steers any more. It tracks whether the session waits for its operator: <b>Busy</b> while a
/// turn runs (no clock); <b>Waiting</b> after a turn ended (a late agent event only restarts the
/// clock); <b>Expecting</b> after a turn ended while guidance had arrived mid-turn — that
/// guidance is the next turn, so the agent's first activity makes it Busy again, and silence
/// means the agent absorbed it and is idle after all. A consumed guidance starts the next turn.
/// </para>
/// </summary>
public sealed class OperatorChannel : IAsyncDisposable
{
    public const string ViewName = "steering";

    private enum Attention { Busy, Expecting, Waiting }

    private readonly IViewPublisher _views;
    private readonly IAsyncDisposable _pump;
    private readonly CancellationTokenSource _halt = new();
    private readonly CancellationTokenSource _end = new();
    private readonly Channel<string> _guidance = Channel.CreateUnbounded<string>();
    private readonly Channel<OperatorSetting> _settings = Channel.CreateUnbounded<OperatorSetting>();
    private readonly Dictionary<string, TaskCompletionSource<IReadOnlyList<OperatorAnswer>>> _pendingForms = [];
    private readonly Lock _gate = new();

    private readonly SessionIdlePolicy? _idle;
    private readonly TimeProvider _time;
    private readonly ITimer? _idleTimer;
    private Attention _attention;
    private bool _guidanceDuringTurn;
    private DateTimeOffset _idleSince;
    private string? _endReason;
    private bool _endAtTurnEnd;

    private OperatorChannel(
        IViewPublisher views, IWorkflowInputs inputs, CancellationToken lifetime, SessionIdlePolicy? idle)
    {
        _views = views;
        _idle = idle;
        _time = idle?.Time ?? TimeProvider.System;
        if (idle is not null)
        {
            _attention = idle.AwaitingFirstTurn ? Attention.Waiting : Attention.Busy;
            _idleSince = _time.GetUtcNow();
            _idleTimer = _time.CreateTimer(_ => OnIdleTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            lock (_gate)
                RearmLocked();
        }
        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var pumpTask = PumpAsync(inputs, pumpCts.Token);
        _pump = new Pump(pumpCts, pumpTask, _idleTimer);
    }

    /// <summary>
    /// Opens the channel and announces the run's steering capabilities to observers.
    /// <paramref name="extraCapabilities"/> extends the baseline set (e.g. "end" for sessions
    /// the operator can finish gracefully); <paramref name="idle"/> ends a turn-based session
    /// nobody steers any more.
    /// </summary>
    public static async Task<OperatorChannel> StartAsync(
        IViewPublisher views, IWorkflowInputs inputs, CancellationToken lifetime = default,
        IReadOnlyList<string>? extraCapabilities = null, SessionIdlePolicy? idle = null)
    {
        var channel = new OperatorChannel(views, inputs, lifetime, idle);
        string[] baseline = [
            SteeringGuidance.TypeName, SteeringFormAnswer.TypeName,
            SteeringHalt.TypeName, SteeringSetting.TypeName];
        await views.PublishAsync(ViewName,
            new SteeringCapabilities([.. baseline, .. extraCapabilities ?? []]), lifetime);
        return channel;
    }

    /// <summary>Cancelled when the operator halts the run from the steering client.</summary>
    public CancellationToken HaltToken => _halt.Token;

    /// <summary>
    /// Cancelled when the session is to end gracefully (multi-turn runs): the operator sent
    /// <c>end</c>, or the session sat idle past its <see cref="SessionIdlePolicy"/>.
    /// </summary>
    public CancellationToken EndToken => _end.Token;

    /// <summary>Why the session is ending (<see cref="SessionEndReasons"/>); null while nobody asked it to.</summary>
    public string? EndReason
    {
        get
        {
            lock (_gate)
                return _endReason;
        }
    }

    /// <summary>
    /// Raises a question form in the steering client and blocks until the operator submits it.
    /// A session that is ending answers on its own: no answer, which reads as a decline.
    /// </summary>
    public async Task<IReadOnlyList<OperatorAnswer>> AskAsync(
        IReadOnlyList<OperatorQuestion> questions, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<IReadOnlyList<OperatorAnswer>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_endReason is not null)
                return [];
            if (!IsWaitingLocked())
                _idleSince = _time.GetUtcNow();
            _pendingForms[requestId] = tcs;
            RearmLocked();
        }

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
            {
                _pendingForms.Remove(requestId);
                RearmLocked();
            }
        }
    }

    /// <summary>
    /// Waits for the next guidance text from the operator. In a turn-based session the guidance
    /// it returns starts the next turn — or, consumed while a turn still runs, queues as the one
    /// after it.
    /// </summary>
    public async ValueTask<string> WaitForGuidanceAsync(CancellationToken cancellationToken)
    {
        var guidance = await _guidance.Reader.ReadAsync(cancellationToken);
        lock (_gate)
        {
            if (_attention == Attention.Busy)
                _guidanceDuringTurn = true;
            else
                _attention = Attention.Busy;
            TouchLocked();
        }
        return guidance;
    }

    /// <summary>The agent is at work (a chat entry, a plan revision) — a turn is running.</summary>
    public void NoteAgentActivity()
    {
        lock (_gate)
        {
            if (_attention == Attention.Expecting)
                _attention = Attention.Busy;
            TouchLocked();
        }
    }

    /// <summary>
    /// Ends the session gracefully, as the operator's <c>end</c> does, with <paramref name="reason"/>
    /// (<see cref="SessionEndReasons"/>; the first reason wins). Open questions are answered with
    /// no answer (a decline) and the running turn finishes first; between turns it ends at once.
    /// </summary>
    public void RequestEnd(string reason)
    {
        List<TaskCompletionSource<IReadOnlyList<OperatorAnswer>>> abandoned;
        bool endNow;
        lock (_gate)
        {
            if (_endReason is not null)
                return;
            _endReason = reason;
            abandoned = [.. _pendingForms.Values];
            endNow = abandoned.Count == 0;
            _endAtTurnEnd = !endNow;
            _idleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        foreach (var form in abandoned)
            form.TrySetResult([]);
        if (endNow)
            CancelEnd();
    }

    /// <summary>Waits for the next live session-setting change from the operator.</summary>
    public ValueTask<OperatorSetting> WaitForSettingAsync(CancellationToken cancellationToken)
        => _settings.Reader.ReadAsync(cancellationToken);

    /// <summary>
    /// Announces the end of the session — observers drop every pending decision card — and why
    /// (<paramref name="reason"/>, else the <see cref="EndReason"/>).
    /// </summary>
    public Task EndSessionAsync(
        bool success, string? error, CancellationToken cancellationToken, string? reason = null)
        => _views.PublishAsync(ViewName,
            new SteeringSessionEnded(success, error, reason ?? EndReason), cancellationToken);

    /// <summary>
    /// Announces that one agent turn finished and the session is idle awaiting operator input —
    /// the steering client's cue to notify the user. From here the idle clock runs. A session
    /// asked to end while the turn was still running ends now instead (no announcement).
    /// </summary>
    public async Task PublishTurnEndedAsync(int turn, string? summary, CancellationToken cancellationToken)
    {
        bool endNow;
        lock (_gate)
        {
            endNow = _endAtTurnEnd;
            if (!endNow)
            {
                _attention = _guidanceDuringTurn ? Attention.Expecting : Attention.Waiting;
                _guidanceDuringTurn = false;
                TouchLocked();
            }
        }
        if (endNow)
        {
            CancelEnd();
            return;
        }
        await _views.PublishAsync(ViewName, new SteeringTurnEnded(turn, summary), cancellationToken);
    }

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
        // protocol is additive, never fatal. Anything else is the operator being there.
        var frame = SteeringCodec.Decode(payload);
        if (frame is not null)
            lock (_gate)
                TouchLocked();
        switch (frame)
        {
            case SteeringGuidance { Text.Length: > 0 } guidance:
                _guidance.Writer.TryWrite(guidance.Text);
                break;

            case SteeringHalt:
                _halt.Cancel();
                break;

            case SteeringEnd:
                RequestEnd(SessionEndReasons.Ended);
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

    private bool IsWaitingLocked() => _attention != Attention.Busy || _pendingForms.Count > 0;

    /// <summary>Something happened — the idle clock starts over.</summary>
    private void TouchLocked()
    {
        if (_idle is null)
            return;
        _idleSince = _time.GetUtcNow();
        RearmLocked();
    }

    private void RearmLocked()
    {
        if (_idleTimer is null || _idle is null)
            return;
        if (_endReason is not null || !IsWaitingLocked())
        {
            _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        var due = _idleSince + _idle.Timeout - _time.GetUtcNow();
        _idleTimer.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void OnIdleTimer()
    {
        lock (_gate)
        {
            if (_idle is null || _endReason is not null || !IsWaitingLocked())
                return;
            if (_time.GetUtcNow() - _idleSince < _idle.Timeout)
            {
                RearmLocked();
                return;
            }
        }
        RequestEnd(SessionEndReasons.IdleTimeout);
    }

    private void CancelEnd()
    {
        try
        {
            _end.Cancel();
        }
        catch (AggregateException)
        {
            // A registration of the agent's threw — it surfaces where the agent observes the end.
        }
        catch (ObjectDisposedException)
        {
            // The session is already torn down.
        }
    }

    private sealed record Pump(CancellationTokenSource Cts, Task Task, ITimer? IdleTimer) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (IdleTimer is not null)
                await IdleTimer.DisposeAsync();
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

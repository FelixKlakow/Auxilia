using System.Text.Json;
using System.Threading.Channels;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// One client's run stream (<c>GET /api/runs/{id}/stream</c>), resumable from a
/// <see cref="RunStreamCursor"/>: every frame carries the position after it, and a client that
/// comes back with it gets everything after it exactly once, in order.
/// <para>
/// <b>View items</b> are ordered by their run-wide sequence (the SDK counts 1, 2, 3, … across all
/// of a run's views). Stored items after the cursor are replayed first; live items from the bus
/// follow, a duplicate is dropped, and an item whose predecessors have not arrived is held back
/// while the store is re-read for them. Predecessors the Core never retained (beyond its per-run
/// cap) are reported as one <c>gap</c> frame after
/// <see cref="CoreApiSettings.StreamGapWaitMilliseconds"/>.
/// </para>
/// <para>
/// <b>Status</b> frames carry the run's state as the Core's record holds it, never a bus event's
/// own copy: a bus transition only prompts a re-read of the record (which another node may still be
/// writing), so a client never sees a state the Core does not hold, never goes back to an older one
/// on a resume, and gets each state once. Transitions that happened while no stream was open
/// collapse into the current state.
/// </para>
/// <para>
/// The run's verdict is the last frame: before it, the stream waits (bounded) until it has every
/// item up to the count the run reported with its final state.
/// </para>
/// </summary>
public sealed class RunStreamSession
{
    /// <summary>How long a status transition seen on the bus is awaited in the record.</summary>
    internal static readonly TimeSpan StatusSettle = TimeSpan.FromSeconds(5);

    /// <summary>One extra store read this long after connecting: items published just before the subscription persist a moment later.</summary>
    internal static readonly TimeSpan SettleRecheck = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan FirstStatusRecheck = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan MaxStatusRecheck = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GapPoll = TimeSpan.FromMilliseconds(200);
    private const int Page = 500;

    private readonly Guid _streamId;
    private readonly ChannelReader<RunStreamEvent> _live;
    private readonly IDataAccess<CoreRunRecord> _runs;
    private readonly IDataAccess<CoreRunViewRecord> _views;
    private readonly Func<string, CancellationToken, Task> _write;
    private readonly TimeProvider _time;
    private readonly TimeSpan _keepalive;
    private readonly TimeSpan _gapWait;
    private readonly ILogger _logger;

    /// <summary>Items that arrived ahead of the next expected sequence.</summary>
    private readonly SortedDictionary<long, RunStreamEvent> _pending = new();

    private CoreRunRecord _record;
    private Guid _recordId;
    private Guid? _instanceId;
    private long _next;
    private string? _statusMark;
    private DateTimeOffset? _gapSince;
    private DateTimeOffset? _gapCheckAt;
    private string? _awaitedMark;
    private DateTimeOffset? _statusCheckAt;
    private DateTimeOffset _statusCheckUntil;
    private TimeSpan _statusBackoff;
    private DateTimeOffset? _settleAt;
    private DateTimeOffset _lastWrite;

    /// <param name="streamId">The id the client subscribed with (dispatch or instance id) — every frame carries it.</param>
    /// <param name="record">The run's record, read after the subscription was registered.</param>
    /// <param name="resumeAfter">Where the client stands; null for a fresh stream.</param>
    /// <param name="live">The run's bus feed (<see cref="RunStreamBroker"/> subscription).</param>
    /// <param name="write">Writes and flushes raw SSE text.</param>
    public RunStreamSession(
        Guid streamId, CoreRunRecord record, RunStreamCursor? resumeAfter,
        ChannelReader<RunStreamEvent> live,
        IDataAccess<CoreRunRecord> runs, IDataAccess<CoreRunViewRecord> views,
        Func<string, CancellationToken, Task> write, TimeProvider time,
        TimeSpan keepalive, TimeSpan gapWait, ILogger logger)
    {
        _streamId = streamId;
        _record = record;
        _recordId = record.Id;
        _next = (resumeAfter?.Sequence ?? 0) + 1;
        _statusMark = resumeAfter?.StatusMark;
        _live = live;
        _runs = runs;
        _views = views;
        _write = write;
        _time = time;
        _keepalive = keepalive;
        _gapWait = gapWait;
        _logger = logger;
    }

    /// <summary>
    /// True when a client at <paramref name="cursor"/> already holds everything the finished run
    /// will ever stream — the endpoint answers 204, which also stops a browser's EventSource.
    /// </summary>
    public static async Task<bool> IsCompleteAsync(
        CoreRunRecord record, RunStreamCursor? cursor, IDataAccess<CoreRunViewRecord> views, CancellationToken ct)
    {
        if (!CoreRunStates.IsTerminal(record.State)
            || cursor is not { StatusMark: { } mark } at
            || mark != RunStreamCursor.MarkOf(record)
            || (record.LastViewSequence is { } last && at.Sequence < last))
            return false;
        var runId = record.Id;
        var after = at.Sequence;
        return !(await views.ReadAsync(ct)).Any(v => v.RunId == runId && v.Sequence > after);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _lastWrite = Now;
        // A live run's state comes first; a finished run's verdict comes last.
        if (!CoreRunStates.IsTerminal(_record.State))
            await EmitStatusIfChangedAsync(ct);
        await BackfillAsync(ct);
        if (CoreRunStates.IsTerminal(_record.State))
        {
            await FinishAsync(ct);
            return;
        }
        _settleAt = Now + SettleRecheck;

        Task<bool>? read = null;
        while (true)
        {
            read ??= _live.WaitToReadAsync(ct).AsTask();
            if (!read.IsCompleted)
                await WaitAsync(read, NextWake(), ct);
            if (read.IsCompleted)
            {
                var hasData = await read;
                read = null;
                if (!hasData)
                    return; // the subscription closed (shutdown)
                while (_live.TryRead(out var evt))
                    await AcceptAsync(evt, ct);
            }

            var now = Now;
            if (_statusCheckAt <= now && await CheckStatusAsync(ct))
                return;
            if (_gapCheckAt <= now)
                await FillGapAsync(ct);
            if (_settleAt <= now)
            {
                _settleAt = null;
                await BackfillAsync(ct);
            }
            if (_keepalive > TimeSpan.Zero && Now - _lastWrite >= _keepalive)
                await WriteAsync(": ping\n\n", ct);
        }
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private DateTimeOffset? NextWake()
    {
        DateTimeOffset? wake = null;
        foreach (var at in new[]
                 {
                     _statusCheckAt, _gapCheckAt, _settleAt,
                     _keepalive > TimeSpan.Zero ? _lastWrite + _keepalive : null,
                 })
            if (at is { } candidate && (wake is null || candidate < wake))
                wake = candidate;
        return wake;
    }

    private async Task WaitAsync(Task read, DateTimeOffset? wake, CancellationToken ct)
    {
        if (wake is not { } at)
        {
            await Task.WhenAny(read);
            return;
        }
        var delay = at - Now;
        if (delay <= TimeSpan.Zero)
            return;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tick = Task.Delay(delay, _time, timer.Token);
        await Task.WhenAny(read, tick);
        await timer.CancelAsync();
        try
        {
            await tick;
        }
        catch (OperationCanceledException)
        {
            // The read won, or the client left — the read reports the latter.
        }
    }

    private async Task AcceptAsync(RunStreamEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case RunStreamEvent.ViewKind:
                if (evt.Sequence < _next)
                    return; // replayed from the store already
                _instanceId ??= evt.AsView()?.WorkflowInstanceId;
                _pending.TryAdd(evt.Sequence, evt);
                await FlushAsync(ct);
                NoteGap();
                return;

            case RunStreamEvent.StatusKind:
                // A transition on the bus: re-read the record until it holds this one.
                _awaitedMark = MarkOfStatusPayload(evt.PayloadJson);
                _statusCheckUntil = Now + StatusSettle;
                _statusBackoff = FirstStatusRecheck;
                _statusCheckAt = Now;
                return;
        }
    }

    /// <summary>Re-reads the record; true when the run is over and its verdict was sent.</summary>
    private async Task<bool> CheckStatusAsync(CancellationToken ct)
    {
        _statusCheckAt = null;
        if (await ResolveRecordAsync(ct) is not { } record)
            return true; // the run was cleared — nothing more will come
        _record = record;
        if (CoreRunStates.IsTerminal(record.State))
        {
            await FinishAsync(ct);
            return true;
        }

        await EmitStatusIfChangedAsync(ct);
        if (RunStreamCursor.MarkOf(record) != _awaitedMark && Now < _statusCheckUntil)
        {
            _statusCheckAt = Now + _statusBackoff;
            _statusBackoff = TimeSpan.FromTicks(Math.Min(_statusBackoff.Ticks * 2, MaxStatusRecheck.Ticks));
        }
        return false;
    }

    /// <summary>The run's record — followed across the claim, which rekeys it onto the instance id.</summary>
    private async Task<CoreRunRecord?> ResolveRecordAsync(CancellationToken ct)
    {
        if (await _runs.ReadAsync(_recordId, ct) is { } current)
            return current;
        var commandId = _record.CommandId ?? _streamId;
        current = (await _runs.ReadAsync(ct)).FirstOrDefault(r => r.CommandId == commandId);
        if (current is not null)
            _recordId = current.Id;
        return current;
    }

    /// <summary>
    /// The id the run's view items are stored under: the runner's instance id — named by every
    /// live item, else the record's id once the claim rekeyed the dispatch record onto it.
    /// </summary>
    private async Task<Guid> ViewRunIdAsync(CancellationToken ct)
    {
        if (_instanceId is { } live)
            return live;
        if (_record.State == RunStates.Dispatched)
            await ResolveRecordAsync(ct);
        return _recordId;
    }

    /// <summary>Delivers stored items from the next expected sequence on (up to <paramref name="upTo"/>).</summary>
    private async Task BackfillAsync(CancellationToken ct, long upTo = long.MaxValue)
    {
        var instanceId = await ViewRunIdAsync(ct);
        var after = _next - 1;
        while (true)
        {
            var from = after;
            var rows = (await _views.ReadAsync(ct))
                .Where(v => v.RunId == instanceId && v.Sequence > from && v.Sequence <= upTo)
                .OrderBy(v => v.Sequence)
                .Take(Page)
                .ToList();
            foreach (var row in rows)
                if (row.Sequence >= _next)
                    _pending.TryAdd(row.Sequence, FromRow(row, instanceId));
            await FlushAsync(ct);
            if (rows.Count < Page)
                break;
            after = rows[^1].Sequence;
        }
        NoteGap();
    }

    private async Task FillGapAsync(CancellationToken ct)
    {
        _gapCheckAt = null;
        if (_pending.Count == 0)
        {
            _gapSince = null;
            return;
        }
        await BackfillAsync(ct, upTo: _pending.Keys.First() - 1);
        if (_pending.Count == 0)
            return;

        var now = Now;
        if (now - _gapSince >= _gapWait)
        {
            await EmitGapAsync(_next, _pending.Keys.First() - 1, ct);
            await FlushAsync(ct);
            _gapSince = _pending.Count > 0 ? now : null;
        }
        _gapCheckAt = _pending.Count > 0 ? now + GapPoll : null;
    }

    /// <summary>The verdict: every item the run published first (bounded wait), then its final status.</summary>
    private async Task FinishAsync(CancellationToken ct)
    {
        var last = _record.LastViewSequence;
        // A run that ended a while ago has nothing left in flight — no point waiting for it.
        var endedAt = _record.CompletedUtc ?? _record.UpdatedUtc;
        var deadline = Now - endedAt > _gapWait ? Now : Now + _gapWait;
        while (true)
        {
            while (_live.TryRead(out var evt))
                if (evt.Kind == RunStreamEvent.ViewKind)
                    await AcceptAsync(evt, ct);
            await BackfillAsync(ct);
            var missing = _pending.Count > 0 || (last is { } count && _next <= count);
            if (!missing || Now >= deadline)
                break;
            await Task.Delay(GapPoll, _time, ct);
        }

        while (_pending.Count > 0)
        {
            await EmitGapAsync(_next, _pending.Keys.First() - 1, ct);
            await FlushAsync(ct);
        }
        if (last is { } final && _next <= final)
            await EmitGapAsync(_next, final, ct);
        await EmitStatusIfChangedAsync(ct);
    }

    private void NoteGap()
    {
        if (_pending.Count == 0)
        {
            _gapSince = null;
            _gapCheckAt = null;
        }
        else if (_gapSince is null)
        {
            _gapSince = Now;
            _gapCheckAt = Now + GapPoll;
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        while (_pending.Remove(_next, out var evt))
        {
            _next = evt.Sequence + 1;
            await WriteFrameAsync(evt with
            {
                RunId = _streamId,
                Cursor = new RunStreamCursor(evt.Sequence, _statusMark).ToString(),
            }, ct);
        }
    }

    private async Task EmitGapAsync(long from, long to, CancellationToken ct)
    {
        if (to < from)
            return;
        _logger.LogWarning(
            "Run stream {RunId}: view items {From}..{To} are not available (not retained by the Core) — reporting a gap.",
            _streamId, from, to);
        _next = to + 1;
        await WriteFrameAsync(new RunStreamEvent(
            RunStreamEvent.GapKind, _streamId, 0,
            JsonSerializer.Serialize(new RunStreamGap(from, to), JsonSerializerOptions.Web), Now)
        {
            Cursor = new RunStreamCursor(to, _statusMark).ToString(),
        }, ct);
    }

    private async Task EmitStatusIfChangedAsync(CancellationToken ct)
    {
        var mark = RunStreamCursor.MarkOf(_record);
        if (mark == _statusMark)
            return;
        _statusMark = mark;
        // The terminal endpoint stays Core-internal — presence rides RunStatus.HasTerminal.
        var status = new WorkflowStatusEvent(
            _record.Id, _record.WorkflowType, _record.State, _record.ErrorMessage, _record.UpdatedUtc,
            _record.OwnerServiceId, _record.CommandId, Reason: _record.Reason);
        await WriteFrameAsync(new RunStreamEvent(
            RunStreamEvent.StatusKind, _streamId, 0,
            JsonSerializer.Serialize(status, JsonSerializerOptions.Web), _record.UpdatedUtc)
        {
            Cursor = new RunStreamCursor(_next - 1, mark).ToString(),
        }, ct);
    }

    private RunStreamEvent FromRow(CoreRunViewRecord row, Guid instanceId)
        => new(RunStreamEvent.ViewKind, _streamId, row.Sequence,
            JsonSerializer.Serialize(
                new ViewDataMessage(instanceId, row.ViewName, row.Sequence, row.PayloadJson), JsonSerializerOptions.Web),
            row.TimestampUtc);

    private static string? MarkOfStatusPayload(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowStatusEvent>(payloadJson, JsonSerializerOptions.Web) is { } status
                ? RunStreamCursor.MarkOf(status.State, status.ErrorMessage, status.TimestampUtc)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task WriteFrameAsync(RunStreamEvent frame, CancellationToken ct)
        => WriteAsync(
            $"id: {frame.Cursor}\ndata: {JsonSerializer.Serialize(frame, JsonSerializerOptions.Web)}\n\n", ct);

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        await _write(text, ct);
        _lastWrite = Now;
    }
}

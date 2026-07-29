namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// A DRIVEN console: the interactive CLI lives in the terminal host (visible through the run's
/// web terminal), but the WORKFLOW feeds it prompts and awaits turn completion via the
/// provider's console events (Claude: the Stop hook). One instance spans many drives, so the
/// agent keeps its full context across pipeline phases; every observed event is also forwarded
/// to <c>onEvent</c> for the run's views.
/// </summary>
public sealed class DrivenConsoleSession(
    ISessionHost host,
    IConsoleSessionPreparer? preparer = null,
    IConsoleSessionEventSource? events = null,
    Func<ConsoleSessionEvent, CancellationToken, Task>? onEvent = null) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private TaskCompletionSource<string>? _turnWaiter;
    private bool _started;

    public async Task StartAsync(TerminalSessionInfo session, CancellationToken cancellationToken)
    {
        if (events is not null)
            await events.StartAsync(OnEventAsync, cancellationToken);
        if (preparer is not null)
            await preparer.PrepareAsync(session.WorkspaceDirectory, cancellationToken);
        await host.StartAsync(session, cancellationToken);
        _started = true;
    }

    /// <summary>
    /// Sends one prompt into the console and awaits the turn's completion; returns the turn's
    /// closing message (empty when the provider's events carry none). Driving without console
    /// events would wait forever — callers must wire an event source.
    /// </summary>
    public async Task<string> DriveAsync(string prompt, CancellationToken cancellationToken)
    {
        if (!_started)
            throw new InvalidOperationException("The console session has not been started.");
        if (events is null)
            throw new InvalidOperationException(
                "Driving needs console events (turn completion) — the provider registered no event source.");

        TaskCompletionSource<string> waiter;
        lock (_gate)
        {
            if (_turnWaiter is { Task.IsCompleted: false })
                throw new InvalidOperationException("A driven turn is already in flight.");
            waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _turnWaiter = waiter;
        }

        await host.SendTextAsync(prompt, cancellationToken);
        await using var cancel = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));

        // A crashed CLI takes the tmux session down and its Stop event never comes — racing
        // the turn against session death turns an eternal hang into a failed run.
        using var sessionWatch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sessionEnd = host.WaitForSessionEndAsync(sessionWatch.Token);
        if (await Task.WhenAny(waiter.Task, sessionEnd) == waiter.Task)
        {
            sessionWatch.Cancel();
            try
            {
                await sessionEnd;
            }
            catch (OperationCanceledException)
            {
                // The watch is only being retired.
            }
            return await waiter.Task;
        }

        await sessionEnd;
        throw new InvalidOperationException(
            "The console session ended while a driven turn was awaiting completion — "
            + "the agent CLI crashed or exited (check its credentials and command).");
    }

    /// <summary>
    /// Sends a CLI command (e.g. <c>/compact</c>) without awaiting a turn — commands do not
    /// end in a Stop event. <paramref name="settle"/> gives the CLI time to process it.
    /// </summary>
    public async Task SendCommandAsync(string command, TimeSpan settle, CancellationToken cancellationToken)
    {
        await host.SendTextAsync(command, cancellationToken);
        await Task.Delay(settle, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
            await host.ShutdownAsync();
        if (events is not null)
            await events.StopAsync();
        lock (_gate)
        {
            _turnWaiter?.TrySetCanceled();
        }
    }

    private async Task OnEventAsync(ConsoleSessionEvent evt, CancellationToken cancellationToken)
    {
        if (evt.Kind == ConsoleSessionEvent.TurnEnded)
        {
            lock (_gate)
            {
                _turnWaiter?.TrySetResult(evt.Message);
            }
        }

        if (onEvent is not null)
            await onEvent(evt, cancellationToken);
    }
}

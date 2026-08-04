namespace Auxilia.AdminConsole.Support;

/// <summary>
/// A poll loop that never dies: every tick failure — including raw transport exceptions from a
/// Core restart — is caught, logged, surfaced via <see cref="IsHealthy"/>, and retried. The
/// effective interval doubles per consecutive failure (capped at 60s) and snaps back on success.
/// Pages keep their own 401/403 semantics inside the tick; transport health lives here.
/// </summary>
public sealed class PagePoller(TimeSpan interval, Func<CancellationToken, Task> tick, ILogger logger) : IDisposable
{
    private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(60);

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public bool IsHealthy { get; private set; } = true;
    public string? LastError { get; private set; }
    public DateTimeOffset? LastSuccessUtc { get; private set; }

    /// <summary>Raised after every tick (success or failure); pages call InvokeAsync(StateHasChanged).</summary>
    public event Action? StateChanged;

    public void Start() => _loop ??= RunLoopAsync();

    /// <summary>One immediate tick outside the timer — the Refresh button and the deterministic test hook.</summary>
    public Task RefreshNowAsync(CancellationToken ct = default) => TickOnceAsync(ct);

    private async Task RunLoopAsync()
    {
        await TickOnceAsync(_cts.Token);
        var consecutiveFailures = IsHealthy ? 0 : 1;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var effective = TimeSpan.FromTicks(Math.Min(
                    MaxInterval.Ticks, interval.Ticks << Math.Min(6, consecutiveFailures)));
                await Task.Delay(effective, _cts.Token);
                await TickOnceAsync(_cts.Token);
                consecutiveFailures = IsHealthy ? 0 : consecutiveFailures + 1;
            }
        }
        catch (OperationCanceledException)
        {
            // Page disposed.
        }
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        try
        {
            await tick(ct);
            IsHealthy = true;
            LastError = null;
            LastSuccessUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            IsHealthy = false;
            LastError = ex.Message;
            logger.LogWarning(ex, "Poll tick failed — retrying with widened interval.");
        }
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

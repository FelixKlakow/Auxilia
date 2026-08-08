using Auxilia.Core.Client;

namespace Auxilia.AdminConsole.Support;

/// <summary>
/// Circuit-scoped ambient state for the shell: how many runs are active right now, and whether the
/// Core is reachable. The sidebar subscribes so the live count and health dot stay current on every
/// page — including the ones that never talk to the run API themselves. One poll loop per circuit,
/// not one per page.
/// </summary>
public sealed class LiveRunMonitor(ICoreClient core, ILogger<LiveRunMonitor> logger) : IDisposable
{
    private PagePoller? _poller;

    public int ActiveRuns { get; private set; }

    public bool CoreHealthy { get; private set; } = true;

    /// <summary>True once a tick has completed — before that the shell renders no claim either way.</summary>
    public bool HasReading { get; private set; }

    public event Action? Changed;

    public void Start()
    {
        if (_poller is not null)
            return;
        _poller = new PagePoller(TimeSpan.FromSeconds(10), TickAsync, logger);
        _poller.StateChanged += () => Changed?.Invoke();
        _poller.Start();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Stats first: a reachable Core that denies run.observe still counts as healthy.
        CoreHealthy = await core.CheckHealthAsync(ct);
        if (CoreHealthy)
        {
            try
            {
                ActiveRuns = (await core.GetRunStatsAsync(ct)).Active;
            }
            catch (CoreApiException)
            {
                ActiveRuns = 0;
            }
        }
        HasReading = true;
    }

    public void Dispose() => _poller?.Dispose();
}

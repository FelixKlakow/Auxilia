namespace Auxilia.Workflows;

/// <summary>
/// Injected into long-living workflow applications. When <see cref="Token"/> fires the
/// instance must finish in-flight work, accept no new triggers, and return from its
/// application delegate — the platform then starts a replacement with the fresh configuration.
/// </summary>
public sealed class WorkflowDrainSignal : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;

    public bool IsDraining => _cts.IsCancellationRequested;

    internal void SignalDrain() => _cts.Cancel();

    public void Dispose() => _cts.Dispose();
}

using Auxilia.Core.Client;
using Auxilia.Core.Contracts;

namespace Auxilia.AdminConsole.Support;

/// <summary>
/// Re-authentication ("step-up") for privileged mutations, shared by every admin page. The Core
/// rejects sensitive changes with <c>elevation-required</c> until the operator re-proves their OWN
/// credential; this stashes the rejected action, and <see cref="ConfirmAsync"/> elevates and retries
/// it so the user never has to repeat the click. The elevation header then holds on this circuit's
/// client for a few minutes of further work.
/// </summary>
public sealed class StepUpFlow(ICoreClient core)
{
    /// <summary>The Core's error detail marking a mutation that needs a fresh credential proof.</summary>
    public const string ElevationRequired = "elevation-required";

    private Func<Task>? _pending;

    public bool IsPending => _pending is not null;

    /// <summary>Bound to the prompt's password box.</summary>
    public string Secret { get; set; } = "";

    /// <summary>Set when the re-authentication itself failed (wrong secret).</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Runs <paramref name="action"/>; on <c>elevation-required</c> stashes it and returns false so
    /// the caller skips its success path and renders the prompt. Every other failure propagates.
    /// </summary>
    public async Task<bool> RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (CoreApiException ex) when (ex.ErrorDetail == ElevationRequired)
        {
            _pending = action;
            Secret = "";
            Error = null;
            return false;
        }
    }

    /// <summary>Proves the caller's credential and retries the stashed action; false = still not done.</summary>
    public async Task<bool> ConfirmAsync()
    {
        var pending = _pending;
        try
        {
            await core.StepUpAsync(new StepUpRequest(Secret));
        }
        catch (CoreApiException ex)
        {
            Error = ex.ErrorDetail ?? ex.Message;
            return false;
        }

        Cancel();
        return pending is not null && await RunAsync(pending);
    }

    public void Cancel()
    {
        _pending = null;
        Secret = "";
        Error = null;
    }
}

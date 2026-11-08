namespace Auxilia.Workflows;

/// <summary>
/// How the run ended, in the workflow's own words: a short reason (e.g. <c>idle-timeout</c>) the
/// SDK reports with the run's final state — clients read it as <c>RunStatus.Reason</c>. The
/// vocabulary is the workflow's; the platform carries it verbatim.
/// </summary>
public interface IRunOutcome
{
    /// <summary>Sets (or replaces) the reason reported with the final state.</summary>
    void SetReason(string reason);
}

/// <summary>The SDK's <see cref="IRunOutcome"/>: holds the last reason set during the run.</summary>
public sealed class RunOutcome : IRunOutcome
{
    private const int MaxLength = 200;
    private string? _reason;

    /// <summary>The reason to report; null when the workflow set none.</summary>
    public string? Reason => Volatile.Read(ref _reason);

    public void SetReason(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var trimmed = reason.Trim();
        Volatile.Write(ref _reason, trimmed.Length > MaxLength ? trimmed[..MaxLength] : trimmed);
    }
}

namespace Auxilia.CodeReview.Workflow.Findings;

public sealed class StagedFindingsStore : IStagedFindingsStore
{
    private readonly List<StagedFinding> _findings = new();
    private readonly object _lock = new();

    /// <summary>Configurable spill threshold. Spill-to-disk deferred to a future iteration.</summary>
    public const long MemoryThresholdBytes = 10 * 1024 * 1024; // 10 MB

    public void Append(StagedFinding finding)
    {
        lock (_lock)
            _findings.Add(finding);
    }

    public IReadOnlyList<StagedFinding> Snapshot()
    {
        lock (_lock)
            return _findings.ToList().AsReadOnly();
    }

    public long ApproximateSizeBytes
    {
        get
        {
            lock (_lock)
                return _findings.Count * 512L; // ~512 bytes average per finding
        }
    }
}

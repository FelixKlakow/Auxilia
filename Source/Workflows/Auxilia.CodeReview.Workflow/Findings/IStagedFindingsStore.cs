namespace Auxilia.CodeReview.Workflow.Findings;

public interface IStagedFindingsStore
{
    /// <summary>Appends a finding atomically. Thread-safe.</summary>
    void Append(StagedFinding finding);

    /// <summary>Returns a point-in-time read-only snapshot of all findings accumulated so far.</summary>
    IReadOnlyList<StagedFinding> Snapshot();

    /// <summary>
    /// Returns the approximate byte size of the current in-memory store.
    /// Intended for monitoring and diagnostic purposes only.
    /// </summary>
    long ApproximateSizeBytes { get; }
}

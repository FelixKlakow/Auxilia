namespace Auxilia.CodeReview.Workflow.Verdicts;

public sealed class VerdictMap
{
    private readonly Dictionary<string, (FileVerdict Verdict, SkipReason? Reason)> _map = new();

    public void Record(string filePath, FileVerdict verdict, SkipReason? reason = null)
        => _map[filePath] = (verdict, reason);

    public IReadOnlyDictionary<string, (FileVerdict Verdict, SkipReason? Reason)> AsReadOnly()
        => _map;
}

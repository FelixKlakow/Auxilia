using Microsoft.Extensions.FileSystemGlobbing;

namespace Auxilia.CodeReview.Workflow.Context;

public sealed class CriticalityClassifier
{
    private readonly IReadOnlyList<Matcher> _matchers;

    public CriticalityClassifier(IReadOnlyList<string> criticalGlobPatterns)
    {
        _matchers = criticalGlobPatterns
            .Select(p =>
            {
                var m = new Matcher();
                m.AddInclude(p);
                return m;
            })
            .ToList()
            .AsReadOnly();
    }

    public FileCriticality Classify(string filePath)
    {
        foreach (var matcher in _matchers)
        {
            if (matcher.Match(filePath).HasMatches)
                return FileCriticality.Critical;
        }
        return FileCriticality.Normal;
    }
}

namespace Auxilia.Workflows.TestRunner;

public sealed record TestRunResult(
    bool Passed,
    int ExitCode,
    int PassCount,
    int FailCount,
    int SkippedCount,
    string LogOutput);

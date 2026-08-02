namespace Auxilia.Workflows.TestRunner;

public sealed record TestRunRequest(
    string Command,
    string? TestFilter = null,
    TimeSpan? Timeout = null);

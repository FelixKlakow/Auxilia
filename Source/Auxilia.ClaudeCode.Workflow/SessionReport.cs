namespace Auxilia.ClaudeCode.Workflow;

/// <summary>The declared "session-report" output: one run's final result as a JSON document.</summary>
public sealed record SessionReport(
    string Instruction,
    bool Success,
    string? Summary,
    int TurnCount,
    decimal? TotalCostUsd,
    long? DurationMs,
    string? ErrorMessage,
    DateTimeOffset CompletedUtc);

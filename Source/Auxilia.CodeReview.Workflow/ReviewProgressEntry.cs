namespace Auxilia.CodeReview.Workflow;

/// <summary>One item of the "progress" log view: a phase transition during the review run.</summary>
public sealed record ReviewProgressEntry(string Phase, string Message, DateTimeOffset Timestamp);

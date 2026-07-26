namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>One line of the workflow's "progress" log view.</summary>
public sealed record SessionProgressEntry(string Phase, string Message, DateTimeOffset TimestampUtc);

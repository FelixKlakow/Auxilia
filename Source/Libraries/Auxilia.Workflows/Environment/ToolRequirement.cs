namespace Auxilia.Workflows.Environment;

public sealed record ToolRequirement(string ToolName, string? MinVersion = null) : IEnvironmentRequirement;

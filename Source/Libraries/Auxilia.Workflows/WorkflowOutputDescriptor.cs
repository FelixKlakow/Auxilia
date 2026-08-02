namespace Auxilia.Workflows;

public sealed record WorkflowOutputDescriptor(
    string Name,
    string RelativePath,
    string? Description);

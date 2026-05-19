namespace Auxilia.Workflows.TaskSource;

public record WorkItem(
    string Id,
    string Title,
    string? Description,
    string? Status,
    string? AssigneeDisplayName,
    IReadOnlyList<string> Labels);

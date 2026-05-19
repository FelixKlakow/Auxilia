namespace Auxilia.Workflows.TaskSource;

/// <summary>A work item read from an external task source.</summary>
/// <param name="Id">Provider-native identifier.</param>
/// <param name="Title">Short display title.</param>
/// <param name="Description">Full description / body text, if available.</param>
/// <param name="Type">Classified item type.</param>
public record WorkItem(string Id, string Title, string? Description, ItemType Type);

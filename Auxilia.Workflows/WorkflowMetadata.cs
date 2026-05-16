namespace Auxilia.Workflows;

public sealed class WorkflowMetadata
{
    public string Version { get; set; } = string.Empty;
    public IList<string> Tags { get; set; } = new List<string>();
}

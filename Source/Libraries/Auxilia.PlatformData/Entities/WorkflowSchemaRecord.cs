using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>Stored workflow schema; one record per workflow type.</summary>
public sealed record WorkflowSchemaRecord : IEntity
{
    public Guid Id { get; init; }
    public required string WorkflowType { get; init; }
    public required string SchemaJson { get; init; }

    public static Guid IdFor(string workflowType) => DeterministicGuid.For("workflow-schema", workflowType);
}

using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// The Core's own catalog of a registered workflow type's schema, built from the runner's
/// <see cref="Auxilia.Workflows.Messaging.Messages.WorkflowSchemaPublished"/> bus events. One record
/// per workflow type. The Core never reads the runner's schema store — it mirrors it over the bus.
/// </summary>
public sealed record CoreWorkflowSchemaRecord : IEntity
{
    public Guid Id { get; init; }
    public required string WorkflowType { get; init; }
    public required string SchemaJson { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }

    public static Guid IdFor(string workflowType) => DeterministicGuid.For("core-workflow-schema", workflowType);
}

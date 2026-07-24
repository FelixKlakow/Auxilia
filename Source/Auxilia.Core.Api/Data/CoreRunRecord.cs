using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// The Core's view of a run, built from the runner's lifecycle status events on the bus. Id is
/// the runner-assigned workflow instance id.
/// </summary>
public sealed record CoreRunRecord : IEntity
{
    public Guid Id { get; init; }
    public required string WorkflowType { get; init; }
    public required string State { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public Guid? ConfigurationId { get; init; }
    public string? ConfigurationName { get; init; }
}

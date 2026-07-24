using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>A first-class local group: a named set of principals that carries roles.</summary>
public sealed record GroupRecord : IEntity
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    public static Guid IdFor(Guid tenantId, string name)
        => DeterministicGuid.For("group", tenantId.ToString("D"), name);
}

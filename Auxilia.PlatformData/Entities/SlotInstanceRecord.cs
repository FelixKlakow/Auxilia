using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// A named, reusable slot configuration ("the team Jira account", "Claude license seat 3"):
/// one provider plus its settings, configured once and referenced by any number of workflow
/// configuration bindings. Settings are protected via <see cref="Protection.ISettingsProtector"/>.
/// </summary>
public sealed record SlotInstanceRecord : IEntity
{
    public Guid Id { get; init; }
    /// <summary>Natural key; the record ID is derived deterministically from it.</summary>
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string ProviderType { get; init; }
    /// <summary>The instance's settings dictionary as JSON, protected via <see cref="Protection.ISettingsProtector"/>.</summary>
    public required string ProtectedSettingsJson { get; init; }
    /// <summary>"Company" = usable by every configurator; "Personal" = owner and assignees only.</summary>
    public string Scope { get; init; } = SlotInstanceScope.Company;
    /// <summary>Creator/owner; personal instances are usable by the owner and the assignees.</summary>
    public Guid? OwnerPrincipalId { get; init; }
    /// <summary>JSON list of principal IDs an administrator assigned this instance to.</summary>
    public string AssignedPrincipalIdsJson { get; init; } = "[]";
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }

    public static Guid IdFor(string name) => DeterministicGuid.For("slot-instance", name);
}

/// <summary>The two visibility scopes of a <see cref="SlotInstanceRecord"/>.</summary>
public static class SlotInstanceScope
{
    public const string Company = "Company";
    public const string Personal = "Personal";
}

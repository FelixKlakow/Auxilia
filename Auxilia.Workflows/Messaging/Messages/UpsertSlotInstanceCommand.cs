namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Seeds (creates or updates) a named, reusable slot instance on the Steering Instance via the
/// <c>slot-configurations</c> exchange — the same seeding family as slot configurations. The
/// instance binds one provider with its settings and is referenced from workflow configuration
/// bindings by ID, so updating it updates every workflow using it.
/// </summary>
public sealed record UpsertSlotInstanceCommand(
    string Name,
    string DisplayName,
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings,
    string Scope = "Company",
    Guid? OwnerPrincipalId = null,
    IReadOnlyList<Guid>? AssignedPrincipalIds = null);

/// <summary>Removes a slot instance; configurations referencing it fail pre-flight until rebound.</summary>
public sealed record RemoveSlotInstanceCommand(string Name);

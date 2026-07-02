using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.BackendService.Dashboard;

/// <summary>One reusable slot instance on the slots page; secret values never leave the store.</summary>
public sealed record SlotInstanceOverview(
    Guid Id,
    string Name,
    string DisplayName,
    string ProviderType,
    string Category,
    string Scope,
    Guid? OwnerPrincipalId,
    IReadOnlyList<Guid> AssignedPrincipalIds,
    IReadOnlyList<SlotInstanceSetting> Settings,
    IReadOnlyList<string> UsedByConfigurations,
    DateTimeOffset UpdatedUtc)
{
    public IReadOnlyList<string> SettingKeys => Settings.Select(s => s.Key).ToList();
}

/// <summary>One stored setting: the value is shown for plain settings, null (masked) for secrets.</summary>
public sealed record SlotInstanceSetting(string Key, string? Value);

/// <summary>Mutable editing model of a slot instance; secret values are write-only.</summary>
public sealed class SlotInstanceDraft
{
    /// <summary>Natural-key name when editing an existing instance; null when creating.</summary>
    public string? ExistingName { get; set; }

    public string DisplayName { get; set; } = "";
    public string ProviderType { get; set; } = "";
    public string Scope { get; set; } = SlotInstanceScope.Company;
    public List<Guid> AssignedPrincipalIds { get; } = [];
    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

    /// <summary>Secret-kind keys that already have a stored value (inputs start empty; empty keeps stored).</summary>
    public HashSet<string> StoredSecretKeys { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Backing service of the reusable slot instances UI. Read path: the shared platform data
/// layer. Write path: the <c>slot-configurations</c> seeding exchange
/// (<see cref="UpsertSlotInstanceCommand"/> / <see cref="RemoveSlotInstanceCommand"/>) so the
/// Steering Instance stays the single writer. Secret settings are write-only; every mutation
/// is audited without settings values.
/// </summary>
public sealed class SlotInstanceService(
    IDataAccess<SlotInstanceRecord> instances,
    IDataAccess<WorkflowConfigurationRecord> configurations,
    IDataAccess<MailboxTriggerRecord> mailboxTriggers,
    ProviderCatalogService catalog,
    ISettingsProtector protector,
    IMessageBusClient messageBus,
    AuditLog auditLog)
{
    internal const string SeedExchangeName = "slot-configurations";

    public async Task<IReadOnlyList<SlotInstanceOverview>> ListAsync(CancellationToken ct = default)
    {
        var catalogEntries = await catalog.ListAsync(ct);
        var categories = catalogEntries
            .ToDictionary(e => e.ProviderType, e => e.Category, StringComparer.Ordinal);
        // Conservative masking: a value renders only when the provider's manifest explicitly
        // declares the key as non-secret. Unknown keys (no descriptor, unknown provider) stay
        // masked — never guess that something is safe to show.
        var plainKeysByProvider = catalogEntries
            .ToDictionary(
                e => e.ProviderType,
                e => e.Descriptors
                    .Where(d => d.Kind != SettingKind.Secret)
                    .Select(d => d.Key)
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        var usage = await UsageAsync(ct);
        return (await instances.ReadAsync(ct)).ToList()
            .Select(record => ToOverview(
                record,
                categories.GetValueOrDefault(record.ProviderType, ""),
                plainKeysByProvider.GetValueOrDefault(record.ProviderType) ?? [],
                usage.GetValueOrDefault(record.Id) ?? []))
            .OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Which configurations reference each instance — through a slot binding or a mailbox
    /// trigger. Deleting an instance that appears here would break them at pre-flight.
    /// </summary>
    private async Task<Dictionary<Guid, List<string>>> UsageAsync(CancellationToken ct)
    {
        var configs = (await configurations.ReadAsync(ct)).ToList();
        var configNamesById = configs.ToDictionary(c => c.Id, c => c.DisplayName);
        var usage = new Dictionary<Guid, List<string>>();

        void Add(Guid instanceId, string configurationName)
        {
            if (!usage.TryGetValue(instanceId, out var names))
                usage[instanceId] = names = [];
            if (!names.Contains(configurationName, StringComparer.Ordinal))
                names.Add(configurationName);
        }

        foreach (var config in configs)
        {
            List<WorkflowConfigurationSlotBinding> bindings;
            try
            {
                bindings = JsonSerializer.Deserialize<List<WorkflowConfigurationSlotBinding>>(
                    config.SlotBindingsJson) ?? [];
            }
            catch (JsonException)
            {
                continue;
            }

            foreach (var binding in bindings.Where(b => b.SlotInstanceId is not null))
                Add(binding.SlotInstanceId!.Value, config.DisplayName);
        }

        foreach (var trigger in (await mailboxTriggers.ReadAsync(ct)).ToList())
        {
            if (trigger.WorkflowConfigurationId is { } configId
                && configNamesById.TryGetValue(configId, out var name))
                Add(trigger.SlotInstanceId, name);
        }

        return usage;
    }

    /// <summary>The instances <paramref name="principalId"/> may bind: company-scoped, own, or assigned.</summary>
    public async Task<IReadOnlyList<SlotInstanceOverview>> AccessibleAsync(
        Guid principalId, CancellationToken ct = default)
        => (await ListAsync(ct)).Where(i => IsAccessible(i, principalId)).ToList();

    internal static bool IsAccessible(SlotInstanceOverview instance, Guid principalId)
        => instance.Scope == SlotInstanceScope.Company
           || instance.OwnerPrincipalId == principalId
           || instance.AssignedPrincipalIds.Contains(principalId);

    /// <summary>Personal instances the principal owns or was assigned — the "my slots" view.</summary>
    public async Task<IReadOnlyList<SlotInstanceOverview>> MineAsync(
        Guid principalId, CancellationToken ct = default)
        => (await ListAsync(ct))
            .Where(i => i.Scope == SlotInstanceScope.Personal
                        && (i.OwnerPrincipalId == principalId || i.AssignedPrincipalIds.Contains(principalId)))
            .ToList();

    /// <summary>
    /// Self-service save: the instance is always personal and owned by the caller; editing
    /// someone else's instance is refused. Admin-made assignments survive the owner's edits.
    /// </summary>
    public async Task<string> SaveOwnAsync(
        Guid principalId, SlotInstanceDraft draft, CancellationToken ct = default)
    {
        draft.Scope = SlotInstanceScope.Personal;
        draft.AssignedPrincipalIds.Clear();
        if (draft.ExistingName is { } name)
        {
            var existing = await instances.ReadAsync(SlotInstanceRecord.IdFor(name), ct)
                           ?? throw new InvalidOperationException("Unknown slot instance.");
            if (existing.OwnerPrincipalId != principalId)
                throw new InvalidOperationException("Only the owner may edit a personal slot instance.");
            draft.AssignedPrincipalIds.AddRange(
                JsonSerializer.Deserialize<List<Guid>>(existing.AssignedPrincipalIdsJson) ?? []);
        }

        return await SaveAsync(principalId.ToString("D"), principalId, draft, ct);
    }

    /// <summary>Self-service delete: owner-only, still guarded by the usage check.</summary>
    public async Task DeleteOwnAsync(Guid principalId, Guid id, CancellationToken ct = default)
    {
        var record = await instances.ReadAsync(id, ct)
                     ?? throw new InvalidOperationException("Unknown slot instance.");
        if (record.OwnerPrincipalId != principalId)
            throw new InvalidOperationException("Only the owner may delete a personal slot instance.");
        await DeleteAsync(principalId.ToString("D"), id, ct);
    }

    /// <summary>Loads an instance for editing; stored secret values are masked, never returned.</summary>
    public async Task<SlotInstanceDraft?> LoadDraftAsync(Guid id, CancellationToken ct = default)
    {
        var record = await instances.ReadAsync(id, ct);
        if (record is null)
            return null;

        var secretKeys = SecretKeysOf(await DescriptorsOfAsync(record.ProviderType, ct));
        var draft = new SlotInstanceDraft
        {
            ExistingName = record.Name,
            DisplayName = record.DisplayName,
            ProviderType = record.ProviderType,
            Scope = record.Scope
        };
        draft.AssignedPrincipalIds.AddRange(
            JsonSerializer.Deserialize<List<Guid>>(record.AssignedPrincipalIdsJson) ?? []);
        foreach (var (key, value) in UnprotectSettings(record.ProtectedSettingsJson))
        {
            if (secretKeys.Contains(key))
            {
                draft.Settings[key] = "";
                if (!string.IsNullOrEmpty(value))
                    draft.StoredSecretKeys.Add(key);
            }
            else
            {
                draft.Settings[key] = value;
            }
        }

        return draft;
    }

    /// <summary>
    /// Validates the draft, merges kept secrets from the stored record, and publishes the
    /// upsert over the bus. Returns the instance's natural-key name.
    /// </summary>
    public async Task<string> SaveAsync(
        string actor, Guid? actorPrincipalId, SlotInstanceDraft draft, CancellationToken ct = default)
    {
        var descriptors = await DescriptorsOfAsync(draft.ProviderType, ct);
        Validate(draft, descriptors);

        var name = draft.ExistingName ?? await NewUniqueNameAsync(draft.DisplayName, ct);
        var existing = draft.ExistingName is null
            ? null
            : await instances.ReadAsync(SlotInstanceRecord.IdFor(draft.ExistingName), ct);

        var settings = draft.Settings
            .Where(s => !string.IsNullOrEmpty(s.Value))
            .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        if (existing is not null && existing.ProviderType == draft.ProviderType)
        {
            var secretKeys = SecretKeysOf(descriptors);
            foreach (var (key, value) in UnprotectSettings(existing.ProtectedSettingsJson))
            {
                if (secretKeys.Contains(key) && !settings.ContainsKey(key) && !string.IsNullOrEmpty(value))
                    settings[key] = value; // keep-unchanged: the user typed nothing new
            }
        }

        var command = new UpsertSlotInstanceCommand(
            name, draft.DisplayName.Trim(), draft.ProviderType, settings,
            draft.Scope, actorPrincipalId ?? existing?.OwnerPrincipalId,
            draft.AssignedPrincipalIds);
        await messageBus.DeclareExchangeAsync(SeedExchangeName, ct);
        await messageBus.PublishToExchangeAsync(SeedExchangeName, command, ct);

        // Audited without settings values: names, provider, scope, and assignment only.
        await auditLog.AppendAsync(actor, "slot-instance.saved", name,
            existing is null && draft.ExistingName is null ? "created" : "updated",
            JsonSerializer.Serialize(new
            {
                providerType = draft.ProviderType,
                scope = draft.Scope,
                assignedPrincipals = draft.AssignedPrincipalIds
            }), ct);

        return name;
    }

    /// <summary>
    /// Publishes the removal over the bus. Refused while any configuration still references
    /// the instance — a deleted-but-referenced instance only surfaces later as a pre-flight
    /// failure, which is the worst possible time.
    /// </summary>
    public async Task DeleteAsync(string actor, Guid id, CancellationToken ct = default)
    {
        var record = await instances.ReadAsync(id, ct)
                     ?? throw new InvalidOperationException("Unknown slot instance.");

        var usedBy = (await UsageAsync(ct)).GetValueOrDefault(id);
        if (usedBy is { Count: > 0 })
            throw new InvalidOperationException(
                $"\"{record.DisplayName}\" is still used by {usedBy.Count} workflow configuration{(usedBy.Count == 1 ? "" : "s")} "
                + $"({string.Join(", ", usedBy)}) — unbind it there first.");

        await messageBus.DeclareExchangeAsync(SeedExchangeName, ct);
        await messageBus.PublishToExchangeAsync(SeedExchangeName, new RemoveSlotInstanceCommand(record.Name), ct);

        await auditLog.AppendAsync(actor, "slot-instance.deleted", record.Name, "removed", ct: ct);
    }

    private void Validate(SlotInstanceDraft draft, IReadOnlyList<SettingDescriptor> descriptors)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(draft.DisplayName))
            errors.Add("Display name is required.");
        if (string.IsNullOrWhiteSpace(draft.ProviderType))
            errors.Add("Pick the provider this instance configures.");
        if (draft.Scope is not (SlotInstanceScope.Company or SlotInstanceScope.Personal))
            errors.Add("Scope must be Company or Personal.");

        foreach (var descriptor in descriptors.Where(d => d.Required))
        {
            var hasValue = !string.IsNullOrWhiteSpace(draft.Settings.GetValueOrDefault(descriptor.Key));
            var keepsStoredSecret = descriptor.Kind == SettingKind.Secret
                                    && draft.StoredSecretKeys.Contains(descriptor.Key);
            if (!hasValue && !keepsStoredSecret)
                errors.Add($"'{descriptor.Label}' is required.");
        }

        if (errors.Count > 0)
            throw new ArgumentException(string.Join("\n", errors));
    }

    private async Task<IReadOnlyList<SettingDescriptor>> DescriptorsOfAsync(
        string providerType, CancellationToken ct)
        => (await catalog.ListAsync(ct))
            .FirstOrDefault(e => e.ProviderType == providerType)?.Descriptors ?? [];

    private static HashSet<string> SecretKeysOf(IReadOnlyList<SettingDescriptor> descriptors)
        => descriptors
            .Where(d => d.Kind == SettingKind.Secret)
            .Select(d => d.Key)
            .ToHashSet(StringComparer.Ordinal);

    private async Task<string> NewUniqueNameAsync(string displayName, CancellationToken ct)
    {
        var slug = WorkflowConfigurationEditorService.Slugify(displayName);
        var existing = (await instances.ReadAsync(ct)).ToList()
            .Select(r => r.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (!existing.Contains(slug))
            return slug;
        for (var i = 2; ; i++)
        {
            var candidate = $"{slug}-{i}";
            if (!existing.Contains(candidate))
                return candidate;
        }
    }

    private SlotInstanceOverview ToOverview(
        SlotInstanceRecord record, string category, HashSet<string> declaredPlainKeys,
        IReadOnlyList<string> usedByConfigurations)
        => new(
            record.Id, record.Name, record.DisplayName, record.ProviderType, category,
            record.Scope, record.OwnerPrincipalId,
            JsonSerializer.Deserialize<List<Guid>>(record.AssignedPrincipalIdsJson) ?? [],
            UnprotectSettings(record.ProtectedSettingsJson)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                // Declared-plain settings show their value so an operator can tell WHICH
                // mailbox/host an instance points at from the list; everything else is masked.
                .Select(kv => new SlotInstanceSetting(
                    kv.Key, declaredPlainKeys.Contains(kv.Key) ? kv.Value : null))
                .ToList(),
            usedByConfigurations,
            record.UpdatedUtc);

    private Dictionary<string, string> UnprotectSettings(string protectedSettingsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                protector.Unprotect(protectedSettingsJson)) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            // Wrong key or foreign format — render as opaque rather than crashing.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}

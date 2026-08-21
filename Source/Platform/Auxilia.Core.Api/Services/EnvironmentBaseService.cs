using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// The admin-managed environment-base catalog: base (name, version) pairs as an open,
/// configurable vocabulary — what editors offer and what layer base-version pins validate
/// against. Managed over the provider-catalog permission, like layers; every mutation audited.
/// </summary>
public sealed class EnvironmentBaseService(
    IDataAccess<EnvironmentBaseRecord> bases,
    AuditLog auditLog,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<EnvironmentBaseDto>> ListAsync(string? search, CancellationToken ct)
        => (await bases.ReadAsync(ct))
            .Where(b => Matches(b, search))
            .OrderBy(b => b.Name, StringComparer.Ordinal)
            .ThenBy(b => b.Version, StringComparer.Ordinal)
            .Select(ToDto)
            .ToList();

    private static bool Matches(EnvironmentBaseRecord record, string? search)
        => string.IsNullOrWhiteSpace(search)
           || Contains(record.Name, search)
           || Contains(record.Version, search)
           || Contains(record.Description, search);

    private static bool Contains(string? value, string search)
        => value?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true;

    public async Task<bool> ExistsAsync(string name, string version, CancellationToken ct)
        => await bases.ReadAsync(
            EnvironmentBaseRecord.IdFor(Normalize(name), version.Trim()), ct) is not null;

    public async Task<EnvironmentBaseDto> UpsertAsync(
        Guid? actor, UpsertEnvironmentBase request, CancellationToken ct)
    {
        var name = Normalize(request.Name ?? "");
        var version = request.Version?.Trim() ?? "";
        if (name.Length == 0)
            throw new ArgumentException("name is required (e.g. linux, windows)");
        if (version.Length == 0)
            throw new ArgumentException("version is required (e.g. ubuntu-24.04)");
        var imageReference = request.ImageReference?.Trim();
        // An image-carrying base is the runtime-spawnable vocabulary of pod control — the same
        // repointing discipline as companion images: only a digest can enter the catalog.
        if (imageReference is { Length: > 0 } && !imageReference.Contains("@sha256:", StringComparison.Ordinal))
            throw new ArgumentException("imageReference must be digest-pinned (…@sha256:…)");

        var record = new EnvironmentBaseRecord
        {
            Id = EnvironmentBaseRecord.IdFor(name, version),
            Name = name,
            Version = version,
            Description = request.Description,
            ImageReference = string.IsNullOrEmpty(imageReference) ? null : imageReference,
            UpdatedUtc = clock.GetUtcNow(),
            UpdatedBy = actor,
        };
        await bases.SaveAsync(record, ct);
        await auditLog.AppendAsync(
            ActorName(actor), "environment-base.upserted", $"{name}/{version}", "upserted", ct: ct);
        return ToDto(record);
    }

    public async Task<bool> DeleteAsync(Guid? actor, string name, string version, CancellationToken ct)
    {
        var removed = await bases.RemoveAsync(
            EnvironmentBaseRecord.IdFor(Normalize(name), version.Trim()), ct);
        if (removed)
            await auditLog.AppendAsync(
                ActorName(actor), "environment-base.deleted", $"{Normalize(name)}/{version.Trim()}", "deleted", ct: ct);
        return removed;
    }

    private static string Normalize(string name) => name.Trim().ToLowerInvariant();

    private static string ActorName(Guid? actor) => actor?.ToString("D") ?? "core-api";

    private static EnvironmentBaseDto ToDto(EnvironmentBaseRecord record)
        => new(record.Name, record.Version, record.Description, record.UpdatedUtc,
            record.ImageReference);

    /// <summary>
    /// The runtime-spawnable base map of a dispatch — <c>name</c> and <c>name/version</c> keys to
    /// digest-pinned images, for every base carrying an image reference. A name key exists only
    /// while it is unambiguous (one image-carrying version); ambiguous names require the
    /// versioned key at spawn time.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> SpawnableImagesAsync(CancellationToken ct)
    {
        var withImages = (await bases.ReadAsync(ct))
            .Where(b => !string.IsNullOrEmpty(b.ImageReference))
            .ToList();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in withImages)
            map[$"{record.Name}/{record.Version}"] = record.ImageReference!;
        foreach (var group in withImages.GroupBy(b => b.Name))
            if (group.Count() == 1)
                map[group.Key] = group.First().ImageReference!;
        return map;
    }
}

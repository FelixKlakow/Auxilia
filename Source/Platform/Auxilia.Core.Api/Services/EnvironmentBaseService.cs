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

        var record = new EnvironmentBaseRecord
        {
            Id = EnvironmentBaseRecord.IdFor(name, version),
            Name = name,
            Version = version,
            Description = request.Description,
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
        => new(record.Name, record.Version, record.Description, record.UpdatedUtc);
}

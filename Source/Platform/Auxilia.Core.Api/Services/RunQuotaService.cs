using System.Collections.Concurrent;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>Thrown when a Run-API quota rejects a dispatch; endpoints map it to 429.</summary>
public sealed class RunQuotaExceededException(string message) : InvalidOperationException(message);

/// <summary>
/// Enforces <see cref="RunQuotaSettings"/> at the single dispatch chokepoint: a platform-wide
/// active-run cap (read from the run store) and a per-principal fixed-window dispatch rate.
/// Denied attempts still count against the window and are audited. System dispatches without a
/// principal (failover redispatch, the approval pipeline) are exempt from the per-principal rate.
/// </summary>
public sealed class RunQuotaService(
    IDataAccess<CoreRunRecord> runs,
    AuditLog auditLog,
    TimeProvider clock,
    IOptions<CoreApiSettings> settings)
{
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset WindowStart, int Count)> _windows = new();

    public async Task EnsureCanDispatchAsync(Guid? principal, CancellationToken ct)
    {
        var quotas = settings.Value.RunQuotas;

        if (quotas.MaxActiveRuns > 0)
        {
            var active = (await runs.ReadAsync(ct)).Count(r => !RunStates.IsTerminal(r.State));
            if (active >= quotas.MaxActiveRuns)
            {
                await auditLog.AppendAsync(
                    principal?.ToString("D") ?? "core-api", "run.quota-exceeded",
                    "max-active-runs", $"{active}/{quotas.MaxActiveRuns}", ct: ct);
                throw new RunQuotaExceededException(
                    $"the platform is at its active-run cap ({quotas.MaxActiveRuns}) — retry once a run finishes");
            }
        }

        if (principal is { } p && quotas.MaxDispatchesPerPrincipalPerMinute > 0)
        {
            var now = clock.GetUtcNow();
            var window = _windows.AddOrUpdate(p,
                _ => (now, 1),
                (_, w) => now - w.WindowStart >= TimeSpan.FromMinutes(1)
                    ? (now, 1)
                    : (w.WindowStart, w.Count + 1));
            if (window.Count > quotas.MaxDispatchesPerPrincipalPerMinute)
            {
                await auditLog.AppendAsync(
                    p.ToString("D"), "run.quota-exceeded",
                    "dispatch-rate", $"{window.Count}/{quotas.MaxDispatchesPerPrincipalPerMinute}/min", ct: ct);
                throw new RunQuotaExceededException(
                    $"dispatch rate exceeded ({quotas.MaxDispatchesPerPrincipalPerMinute}/min) — slow down and retry");
            }
        }
    }
}

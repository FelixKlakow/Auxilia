using System.Security.Cryptography;
using System.Text;
using Auxilia.PlatformData;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Scoped download tokens for a PENDING Core-stored package under approval review. The verdict
/// run executes in a workflow container that holds no principal credential and no resolution
/// token for the package under review (its own resolution token covers its OWN package), so the
/// approval pipeline mints one of these and passes a fetchable URL into the run's dispatch
/// context. A token is bound to ONE workflow type AND ONE submission (its package hash — a
/// re-registration invalidates it) and expires with the approval evaluation window (the verdict
/// timeout plus a small grace). Tokens are SELF-VALIDATING (HMAC over type + package hash +
/// expiry, same construction as <see cref="TerminalTicketService"/>) so they verify on
/// every Core.Api node without shared state. The MAC key derives from the shared
/// settings-protection key; without one (dev) a per-process key keeps single-node behaviour.
/// </summary>
public sealed class PendingPackageDownloadTokenService(
    TimeProvider clock, PlatformDataSettings dataSettings, IOptions<CoreApiSettings> settings)
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private readonly byte[] _key = string.IsNullOrWhiteSpace(dataSettings.ProtectionKeyBase64)
        ? RandomNumberGenerator.GetBytes(32)
        : SHA256.HashData([
            .. Convert.FromBase64String(dataSettings.ProtectionKeyBase64),
            .. "auxilia-pending-package-download"u8
        ]);

    /// <summary>A token admitting downloads of ONE submission (type + package hash) for the evaluation window.</summary>
    public string Issue(string workflowType, string? packageHashBase64)
    {
        var timeToLive = TimeSpan.FromSeconds(
            Math.Max(60, settings.Value.ApprovalVerdictWorkflow.TimeoutSeconds)) + Grace;
        var expires = clock.GetUtcNow() + timeToLive;
        var payload = expires.UtcTicks.ToString();
        return $"{payload}.{MacFor(workflowType, packageHashBase64, payload)}";
    }

    /// <summary>True when the token is authentic, unexpired, and was minted for THIS type and THIS package.</summary>
    public bool Validate(string? token, string workflowType, string? packageHashBase64)
    {
        if (token?.Split('.') is not [var ticksRaw, var mac])
            return false;
        if (!long.TryParse(ticksRaw, out var ticks)
            || new DateTimeOffset(ticks, TimeSpan.Zero) <= clock.GetUtcNow())
            return false;

        var expected = MacFor(workflowType, packageHashBase64, ticksRaw);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(mac));
    }

    private string MacFor(string workflowType, string? packageHashBase64, string payload)
        => Convert.ToHexStringLower(
            HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{workflowType}\n{packageHashBase64}\n{payload}")));
}

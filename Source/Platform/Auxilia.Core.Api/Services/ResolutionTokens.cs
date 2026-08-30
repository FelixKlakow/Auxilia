using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// The Core stores only a SHA-256 digest of a run's resolution token: the token is a bearer
/// capability for the run's slot credentials, so at rest the Core keeps what it needs to VERIFY
/// a presented token, never the token itself. The persisted dispatch command is redacted the
/// same way — a failover/rerun re-dispatch always mints a fresh token and package URL, so the
/// stored copy never needs the originals.
/// </summary>
internal static partial class ResolutionTokens
{
    public static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Fixed-time comparison of a presented token against the stored digest.</summary>
    public static bool Matches(string storedHash, string presentedToken)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(storedHash),
            Encoding.UTF8.GetBytes(Hash(presentedToken)));

    /// <summary>
    /// The command as persisted: no resolution token, and the token-authorized package download
    /// URL loses its token query. Only the bus copy the runner receives carries the plaintext.
    /// </summary>
    public static RunWorkflowCommand Redacted(RunWorkflowCommand command)
        => command with
        {
            ResolutionToken = null,
            WorkflowPackageUri = command.WorkflowPackageUri is { } uri
                ? PackageTokenQuery().Replace(uri, "redacted")
                : null,
        };

    [GeneratedRegex(@"(?<=[?&]token=)[^&]+")]
    private static partial Regex PackageTokenQuery();
}

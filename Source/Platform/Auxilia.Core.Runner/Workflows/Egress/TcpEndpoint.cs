using System.Net;
using System.Text.RegularExpressions;

namespace Auxilia.Core.Runner.Workflows.Egress;

/// <summary>
/// A raw-TCP egress endpoint, <c>tcp://host:port</c> — any endpoint source (workflow manifest,
/// run configuration, plugin manifest) may declare one. The host must be a NAME: the relay works
/// by resolving that name to the gateway inside the workload, which an address cannot do.
/// </summary>
public sealed partial record TcpEndpoint(string Host, int Port)
{
    private const string Scheme = "tcp://";

    public static bool IsTcp(string entry) => entry.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <exception cref="EgressPlanException">The entry is not <c>tcp://name:port</c>.</exception>
    public static TcpEndpoint Parse(string entry)
    {
        var match = Shape().Match(entry);
        if (!match.Success
            || IPAddress.TryParse(match.Groups["host"].Value, out _)
            || !int.TryParse(match.Groups["port"].Value, out var port) || port is < 1 or > 65535)
            throw new EgressPlanException(
                $"TCP endpoint '{entry}' must be tcp://<host name>:<port> (addresses cannot be relayed by name)");
        return new TcpEndpoint(match.Groups["host"].Value.ToLowerInvariant(), port);
    }

    /// <summary>The host an allowlist entry names (scheme and port stripped).</summary>
    public static string HostOf(string entry)
    {
        var value = IsTcp(entry) ? entry[Scheme.Length..] : entry;
        if (value.StartsWith('[') && value.IndexOf(']') is > 0 and var close)
            return value[1..close];
        var colon = value.IndexOf(':');
        return colon > 0 && colon == value.LastIndexOf(':') ? value[..colon] : value;
    }

    public override string ToString() => $"{Scheme}{Host}:{Port}";

    [GeneratedRegex(@"^tcp://(?<host>[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?):(?<port>\d{1,5})$", RegexOptions.IgnoreCase)]
    private static partial Regex Shape();
}

/// <summary>The run's egress cannot be arranged as declared — the dispatch fails pre-flight with this reason.</summary>
public sealed class EgressPlanException(string message) : Exception(message);

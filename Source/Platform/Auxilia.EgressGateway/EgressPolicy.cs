using System.Net;
using System.Net.Sockets;

namespace Auxilia.EgressGateway;

/// <summary>
/// The run's effective egress allowlist, as data: <c>*</c> (allow-all), <c>host</c>,
/// <c>host:port</c>, <c>*.suffix</c>, <c>*.suffix:port</c>, an IP literal, or an http(s) URL
/// (reduced to its host and port). A host without a port allows every port.
/// Address rules hold in every mode: loopback, link-local (cloud metadata), multicast and
/// unspecified addresses are reachable only as an explicitly listed IP literal; private and
/// internal addresses (RFC 1918, CGNAT, unique-local — where the platform's own services and
/// other runs live) only through an explicit entry, never through the allow-all wildcard.
/// Protected endpoints (the platform's own services, e.g. the broker) are refused before any
/// allow entry — by name and by resolved address — so a run reaches them only through their
/// dedicated relays, even when it may reach other ports of the same host.
/// </summary>
public sealed class EgressPolicy
{
    private readonly List<Entry> _entries;
    private readonly List<Entry> _protected = [];

    public bool AllowAll { get; }

    private sealed record Entry(string Host, bool Wildcard, int? Port, bool IsAddress);

    /// <summary>The outcome of a name-level check: whether it passes, and whether an explicit entry matched.</summary>
    public readonly record struct Match(bool Allowed, bool Explicit);

    private EgressPolicy(List<Entry> entries, bool allowAll)
    {
        _entries = entries;
        AllowAll = allowAll;
    }

    public static EgressPolicy Parse(string? allowList, string? protectedList = null)
    {
        var entries = ParseEntries(allowList, out var allowAll);
        var policy = new EgressPolicy(entries, allowAll);
        policy._protected.AddRange(ParseEntries(protectedList, out _));
        return policy;
    }

    private static List<Entry> ParseEntries(string? list, out bool allowAll)
    {
        var entries = new List<Entry>();
        allowAll = false;
        foreach (var raw in (list ?? string.Empty).Split(
                     [',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*")
            {
                allowAll = true;
                continue;
            }
            var (host, port) = SplitHostPort(raw);
            if (host.Length == 0)
                continue;
            var wildcard = host.StartsWith("*.", StringComparison.Ordinal);
            entries.Add(new Entry(
                wildcard ? host[1..] : host, wildcard, port, IPAddress.TryParse(host, out _)));
        }
        return entries;
    }

    /// <summary>Whether <paramref name="host"/>:<paramref name="port"/> names a protected platform endpoint.</summary>
    public bool IsProtected(string host, int port)
    {
        host = Normalize(host);
        return _protected.Any(e => (e.Port is null || e.Port == port)
                                   && string.Equals(host, e.Host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether any of <paramref name="addresses"/> on <paramref name="port"/> is a protected
    /// endpoint's address — another name for the broker is still the broker.
    /// </summary>
    public async Task<bool> IsProtectedAddressAsync(IReadOnlyCollection<IPAddress> addresses, int port, CancellationToken ct)
    {
        foreach (var entry in _protected.Where(e => e.Port is null || e.Port == port))
        {
            IPAddress[] resolved;
            try
            {
                resolved = IPAddress.TryParse(entry.Host, out var literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(entry.Host, ct);
            }
            catch (System.Net.Sockets.SocketException)
            {
                continue; // an unresolvable protected name protects no address
            }
            if (resolved.Any(r => addresses.Any(a => Unmapped(a).Equals(Unmapped(r)))))
                return true;
        }
        return false;
    }

    private static IPAddress Unmapped(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>Whether <paramref name="host"/>:<paramref name="port"/> may be connected to (name level).</summary>
    public bool Allows(string host, int port) => Check(host, port).Allowed;

    public Match Check(string host, int port)
    {
        host = Normalize(host);
        foreach (var entry in _entries)
        {
            if (entry.Port is { } p && p != port)
                continue;
            if (entry.Wildcard
                    ? host.EndsWith(entry.Host, StringComparison.OrdinalIgnoreCase) && host.Length > entry.Host.Length
                    : string.Equals(host, entry.Host, StringComparison.OrdinalIgnoreCase))
                return new Match(true, true);
        }
        return new Match(AllowAll, false);
    }

    /// <summary>
    /// Whether a resolved address may be dialled for <paramref name="host"/>: special-purpose
    /// addresses only when the operator listed that very address as an IP literal; private or
    /// internal addresses only when an explicit entry admitted the destination.
    /// </summary>
    public bool AllowsAddress(string host, IPAddress address, bool explicitMatch = true)
    {
        if (IsSpecialPurpose(address))
            return _entries.Any(e => e.IsAddress && IPAddress.TryParse(e.Host, out var listed) && listed.Equals(address))
                   && IPAddress.TryParse(Normalize(host), out var literal) && literal.Equals(address);
        return explicitMatch || !IsPrivate(address);
    }

    internal static bool IsSpecialPurpose(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254 // link-local (cloud metadata endpoints)
               || bytes[0] >= 224;                // multicast / reserved
    }

    /// <summary>Private and internal ranges: where platform services, the operator's network and other runs live.</summary>
    internal static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 unique-local
        var b = address.GetAddressBytes();
        return b[0] == 10
               || b[0] == 172 && b[1] >= 16 && b[1] <= 31
               || b[0] == 192 && b[1] == 168
               || b[0] == 100 && b[1] >= 64 && b[1] <= 127; // CGNAT
    }

    internal static (string Host, int? Port) SplitHostPort(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return (Normalize(uri.Host), uri.IsDefaultPort ? null : uri.Port);
        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon && int.TryParse(value[(colon + 1)..], out var port))
            return (Normalize(value[..colon]), port);
        return (Normalize(value), null);
    }

    private static string Normalize(string host) => host.Trim().TrimEnd('.').Trim('[', ']').ToLowerInvariant();
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Auxilia.DockerGuard;

/// <summary>
/// The guard's verdict on one Docker API call. An allowed call with a <see cref="Body"/> is
/// forwarded with that body instead of the caller's (host binds carried out beneath their root).
/// </summary>
public sealed record GuardDecision(bool Allowed, string? Reason, byte[]? Body = null)
{
    public static readonly GuardDecision Allow = new(true, null);

    public static GuardDecision Deny(string reason) => new(false, reason);

    public static GuardDecision AllowAs(byte[] body) => new(true, null, body);
}

/// <summary>An existing network as the daemon reports it.</summary>
public sealed record DockerNetwork(string Id, string Name, IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// The daemon objects the policy consults — labels of existing objects, the daemon's own answer
/// and never the caller's claim — and the root volumes the guard keeps for its bind roots.
/// </summary>
public interface IDockerObjects
{
    Task<IReadOnlyDictionary<string, string>?> ContainerLabelsAsync(string id, CancellationToken ct);

    Task<DockerNetwork?> NetworkAsync(string idOrName, CancellationToken ct);

    Task<IReadOnlyDictionary<string, string>?> VolumeLabelsAsync(string name, CancellationToken ct);

    /// <summary>
    /// The volume binding host directory <paramref name="root"/>, created by the guard on first
    /// use (the runner may never set driver options). Throws when the daemon cannot mount volume
    /// subpaths or a volume of that name binds something else.
    /// </summary>
    Task<string> RootVolumeAsync(string root, CancellationToken ct);
}

/// <summary>
/// The Docker API a runner may use — exactly the calls it makes, nothing else: no exec, no
/// attach, no commit, no update. Everything it creates must carry an ownership label and the
/// runner's confinement (<see cref="ContainerCreatePolicy"/>); it may act only on existing
/// objects that carry an ownership label; builds and image deletions are limited to its composed
/// environment tags, pulls to digest references. A compromised runner process therefore cannot
/// turn the daemon into host root, read or steer other applications' containers, or overwrite
/// an image it launches by tag.
/// </summary>
public sealed class DockerApiPolicy(DockerGuardSettings settings, IDockerObjects objects)
{
    private static readonly Regex VersionPrefix = new(@"^/v(?<version>\d+(\.\d+)?)(?=/)", RegexOptions.CultureInvariant);
    private static readonly Regex ContainerOperation =
        new(@"^/containers/(?<id>[^/]+)/(?<op>[^/]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Container = new(@"^/containers/(?<id>[^/]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex ImageInspect = new(@"^/images/(?<name>.+)/json$", RegexOptions.CultureInvariant);
    private static readonly Regex Image = new(@"^/images/(?<name>.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex NetworkMembership =
        new(@"^/networks/(?<id>[^/]+)/(?<op>connect|disconnect)$", RegexOptions.CultureInvariant);
    private static readonly Regex Network = new(@"^/networks/(?<id>[^/]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex Volume = new(@"^/volumes/(?<name>[^/]+)$", RegexOptions.CultureInvariant);

    /// <summary>Container operations the runner performs, with the methods it uses for each.</summary>
    private static readonly Dictionary<string, string[]> ContainerOperations = new(StringComparer.Ordinal)
    {
        ["json"] = ["GET"],
        ["start"] = ["POST"],
        ["stop"] = ["POST"],
        ["kill"] = ["POST"],
        ["wait"] = ["POST"],
        ["logs"] = ["GET"],
        ["archive"] = ["GET", "HEAD", "PUT"],
    };

    /// <summary>The API path without its version prefix (<c>/v1.47/containers/create</c> → <c>/containers/create</c>).</summary>
    public static string Normalize(string path) => VersionPrefix.Replace(path, "");

    /// <summary>Whether the call's JSON body decides it (and must therefore be read before forwarding).</summary>
    public static bool NeedsBody(string method, string path)
    {
        path = Normalize(path);
        return string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
               && (path is "/containers/create" or "/networks/create" or "/volumes/create"
                   || NetworkMembership.IsMatch(path));
    }

    public async Task<GuardDecision> DecideAsync(
        string method, string path, IReadOnlyDictionary<string, string[]> query, JsonElement? body, CancellationToken ct)
    {
        method = method.ToUpperInvariant();
        // Kestrel hands over the decoded path with dot segments resolved — except an encoded
        // slash, which it keeps (and a decoded "%25" leaves a '%'). The daemon would decode
        // either into a path the rules never saw, so no such path is forwarded at all.
        if (path.Contains('%') || path.Contains('\\') || path.Split('/').Any(segment => segment is "." or ".."))
            return GuardDecision.Deny("the request path is not one a runner sends");
        if (VersionPrefix.Match(path) is { Success: true } prefix
            && (!TryParseVersion(prefix.Groups["version"].Value, out var requested)
                || !TryParseVersion(settings.MinimumApiVersion, out var minimum) || requested < minimum))
            return GuardDecision.Deny(
                $"Engine API v{prefix.Groups["version"].Value} is older than the guard serves (v{settings.MinimumApiVersion})");
        path = Normalize(path);
        // Read the body exactly as the daemon will (case-insensitive names): anything a reader
        // could see two ways is refused before a rule looks at it.
        if (body is { } inspected && JsonBody.Ambiguity(inspected) is { } ambiguity)
            return GuardDecision.Deny($"ambiguous request body: {ambiguity}");

        switch (method, path)
        {
            case ("GET" or "HEAD", "/_ping"):
            case ("GET", "/version"):
            case ("GET", "/info"):
            case ("GET", "/containers/json"):
            case ("GET", "/images/json"):
            case ("GET", "/networks"):
            case ("GET", "/volumes"):
                return GuardDecision.Allow;
            case ("POST", "/containers/create"):
                return body is { } containerBody
                    ? await ContainerCreatePolicy.DecideAsync(containerBody, settings, objects, ct)
                    : GuardDecision.Deny("a container is created from a JSON body");
            case ("POST", "/networks/create"):
                return DecideNetworkCreate(body);
            case ("POST", "/volumes/create"):
                return DecideVolumeCreate(body);
            case ("POST", "/images/create"):
                return DecidePull(query);
            case ("POST", "/build"):
                return DecideBuild(query);
        }

        if (ContainerOperation.Match(path) is { Success: true } op)
            return ContainerOperations.TryGetValue(op.Groups["op"].Value, out var methods) && methods.Contains(method)
                ? await OwnedContainerAsync(op.Groups["id"].Value, ct)
                : GuardDecision.Deny($"container operation '{op.Groups["op"].Value}' is not one the runner makes");
        if (Container.Match(path) is { Success: true } container && method == "DELETE")
            return await OwnedContainerAsync(container.Groups["id"].Value, ct);
        if (method == "GET" && ImageInspect.IsMatch(path))
            return GuardDecision.Allow;
        if (method == "DELETE" && Image.Match(path) is { Success: true } image)
            return Uri.UnescapeDataString(image.Groups["name"].Value).StartsWith(settings.BuildTagPrefix, StringComparison.Ordinal)
                ? GuardDecision.Allow
                : GuardDecision.Deny($"only composed environment images ({settings.BuildTagPrefix}*) may be deleted");
        if (method == "POST" && NetworkMembership.Match(path) is { Success: true } membership)
            return await DecideMembershipAsync(membership.Groups["id"].Value, body, ct);
        if (Network.Match(path) is { Success: true } network)
            return method switch
            {
                "GET" => GuardDecision.Allow,
                "DELETE" => await OwnedNetworkAsync(network.Groups["id"].Value, ct) is not null
                    ? GuardDecision.Allow
                    : GuardDecision.Deny("only Auxilia's own networks may be removed"),
                _ => GuardDecision.Deny("not a network operation the runner makes"),
            };
        if (Volume.Match(path) is { Success: true } volumeName)
            return method switch
            {
                "GET" => GuardDecision.Allow,
                "DELETE" => IsOwned(await objects.VolumeLabelsAsync(volumeName.Groups["name"].Value, ct))
                    ? GuardDecision.Allow
                    : GuardDecision.Deny("only Auxilia's own volumes may be removed"),
                _ => GuardDecision.Deny("not a volume operation the runner makes"),
            };

        return GuardDecision.Deny($"{method} {path} is not an operation the runner makes");
    }

    private GuardDecision DecideNetworkCreate(JsonElement? body)
    {
        if (body is not { } network || !OwnedLabels(network, "Labels"))
            return GuardDecision.Deny("a network carries an Auxilia ownership label");
        // The runner creates run and pod networks only — internal bridges with the daemon's
        // defaults, no route out (a macvlan/ipvlan driver or bridge options could attach them
        // to the host's own network).
        if (!JsonBody.IsTrue(network, "Internal"))
            return GuardDecision.Deny("a runner network is internal");
        if (JsonBody.String(network, "Driver") is { Length: > 0 } driver && driver != "bridge")
            return GuardDecision.Deny($"network driver '{driver}' is not the bridge driver");
        if (JsonBody.IsNonEmpty(network, "Options"))
            return GuardDecision.Deny("network driver options are never set by a runner");
        if (JsonBody.TryGet(network, "IPAM", out var ipam)
            && ((JsonBody.String(ipam, "Driver") is { Length: > 0 } ipamDriver && ipamDriver != "default")
                || JsonBody.IsNonEmpty(ipam, "Options")))
            return GuardDecision.Deny("network address management stays the daemon's default");
        if (JsonBody.IsTrue(network, "Ingress") || JsonBody.IsTrue(network, "ConfigOnly")
            || JsonBody.IsNonEmpty(network, "ConfigFrom")
            || (JsonBody.String(network, "Scope") is { Length: > 0 } scope && scope != "local"))
            return GuardDecision.Deny("not a runner network");
        return GuardDecision.Allow;
    }

    private GuardDecision DecideVolumeCreate(JsonElement? body)
    {
        if (body is not { } volume || !OwnedLabels(volume, "Labels"))
            return GuardDecision.Deny("a volume carries an Auxilia ownership label");
        // Driver options turn a volume into a mount of anything — "o=bind,device=/" is the host.
        if (JsonBody.String(volume, "Driver") is { Length: > 0 } driver && driver != "local")
            return GuardDecision.Deny($"volume driver '{driver}' is not the daemon's local driver");
        if (JsonBody.IsNonEmpty(volume, "DriverOpts"))
            return GuardDecision.Deny("volume driver options could mount any host directory");
        if (JsonBody.IsSet(volume, "ClusterVolumeSpec"))
            return GuardDecision.Deny("not a runner volume");
        return GuardDecision.Allow;
    }

    private static GuardDecision DecidePull(IReadOnlyDictionary<string, string[]> query)
    {
        // A pull fetches the named repository from its own registry, so it can change no other
        // name. An import (fromSrc) names its own repository and tag — it could re-point any.
        if (query.ContainsKey("fromSrc") || query.ContainsKey("repo") || query.ContainsKey("changes"))
            return GuardDecision.Deny("images are pulled from a registry, never imported");
        return First(query, "fromImage") is { Length: > 0 }
            ? GuardDecision.Allow
            : GuardDecision.Deny("a pull names its image");
    }

    private GuardDecision DecideBuild(IReadOnlyDictionary<string, string[]> query)
    {
        var tags = query.TryGetValue("t", out var t) ? t : [];
        if (tags.Length == 0 || tags.Any(tag => !tag.StartsWith(settings.BuildTagPrefix, StringComparison.Ordinal)))
            return GuardDecision.Deny($"builds produce composed environment images ({settings.BuildTagPrefix}*) only");
        if (First(query, "networkmode") is { Length: > 0 } mode && mode is not ("default" or "bridge" or "none"))
            return GuardDecision.Deny($"a build stays on the daemon's default network (not '{mode}')");
        if (First(query, "cgroupparent") is { Length: > 0 })
            return GuardDecision.Deny("a build never chooses its cgroup parent");
        if (First(query, "remote") is { Length: > 0 })
            return GuardDecision.Deny("builds come from the runner's own context, never a remote");
        return GuardDecision.Allow;
    }

    private async Task<GuardDecision> DecideMembershipAsync(string networkId, JsonElement? body, CancellationToken ct)
    {
        var containerId = body is { } b ? JsonBody.String(b, "Container") : null;
        if (containerId is null || (await OwnedContainerAsync(containerId, ct)).Allowed is false)
            return GuardDecision.Deny("only Auxilia's own containers join or leave networks");
        // The daemon joins the network an EndpointConfig.NetworkID names, not the one in the path.
        if (body is { } membership && JsonBody.TryGet(membership, "EndpointConfig", out var endpoint)
            && ContainerCreatePolicy.EndpointRefusal(endpoint) is { } endpointRefusal)
            return GuardDecision.Deny(endpointRefusal);
        return await MayJoinAsync(networkId, ct)
            ? GuardDecision.Allow
            : GuardDecision.Deny("an Auxilia container joins only Auxilia's networks and the configured outbound network");
    }

    /// <summary>Whether a runner's container may be attached to <paramref name="idOrName"/>.</summary>
    internal async Task<bool> MayJoinAsync(string idOrName, CancellationToken ct)
    {
        if (settings.OutboundNetworks.Contains(idOrName, StringComparer.Ordinal))
            return true;
        var network = await objects.NetworkAsync(idOrName, ct);
        return network is not null
               && (IsOwned(network.Labels) || settings.OutboundNetworks.Contains(network.Name, StringComparer.Ordinal));
    }

    private async Task<GuardDecision> OwnedContainerAsync(string id, CancellationToken ct)
        => IsOwned(await objects.ContainerLabelsAsync(Uri.UnescapeDataString(id), ct))
            ? GuardDecision.Allow
            : GuardDecision.Deny("the container is not an Auxilia runner's");

    private async Task<DockerNetwork?> OwnedNetworkAsync(string id, CancellationToken ct)
        => await objects.NetworkAsync(id, ct) is { } network && IsOwned(network.Labels) ? network : null;

    internal bool IsOwned(IReadOnlyDictionary<string, string>? labels)
        => labels is not null && settings.OwnershipLabels.Any(label =>
        {
            var eq = label.IndexOf('=');
            return eq > 0 && labels.TryGetValue(label[..eq], out var value) && value == label[(eq + 1)..];
        });

    internal bool OwnedLabels(JsonElement element, string property)
        => IsOwned(JsonBody.StringMap(element, property));

    private static string? First(IReadOnlyDictionary<string, string[]> query, string key)
        => query.TryGetValue(key, out var values) && values.Length > 0 ? values[0] : null;

    /// <summary>An Engine API version ("1.45", "1") as a comparable <see cref="Version"/>.</summary>
    internal static bool TryParseVersion(string text, out Version version)
        => Version.TryParse(text.Contains('.') ? text : text + ".0", out version!);

    /// <summary>
    /// The query string as the daemon parses it (Go's <c>url.ParseQuery</c>): keys are
    /// case-SENSITIVE and a key's first value is the one read. ASP.NET Core's own query
    /// collection merges keys case-insensitively, which would let
    /// <c>NetworkMode=x&amp;networkmode=host</c> hide the value the daemon acts on.
    /// </summary>
    public static Dictionary<string, string[]> ParseQuery(string? queryString)
    {
        var pairs = (queryString ?? "").TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var eq = pair.IndexOf('=');
                return (Key: Unescape(eq < 0 ? pair : pair[..eq]), Value: Unescape(eq < 0 ? "" : pair[(eq + 1)..]));
            });
        return pairs.GroupBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Value).ToArray(), StringComparer.Ordinal);

        static string Unescape(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
    }
}

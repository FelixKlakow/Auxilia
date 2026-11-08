using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Auxilia.DockerGuard.JsonBody;

namespace Auxilia.DockerGuard;

/// <summary>
/// What a container the runner creates must look like: owned (an ownership label), confined
/// (<c>no-new-privileges</c> and no other security option, never privileged, no capability beyond
/// the allowed set, no host namespace, no device, no protective default overridden) and bounded
/// in what it touches (host directories only below the configured roots — carried out so the
/// daemon resolves them beneath the root —, volumes only Auxilia's own and without driver
/// options, networks only Auxilia's own or the outbound one, published ports only on loopback,
/// no log shipping). A request that sets anything the runner never sets is refused — the guard
/// holds the line even when the runner does not.
/// </summary>
internal static class ContainerCreatePolicy
{
    private static readonly Regex DrivePath = new(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant);

    /// <summary>
    /// The container configuration the runner sends (the Engine API's <c>Config</c> plus the
    /// host and networking configurations). Anything else is refused — notably the deprecated
    /// top-level copies of host-configuration fields some daemon versions still honour.
    /// </summary>
    private static readonly HashSet<string> TopLevelFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Hostname", "Domainname", "User", "AttachStdin", "AttachStdout", "AttachStderr", "ExposedPorts",
        "Tty", "OpenStdin", "StdinOnce", "Env", "Cmd", "Healthcheck", "ArgsEscaped", "Image", "Volumes",
        "WorkingDir", "Entrypoint", "NetworkDisabled", "MacAddress", "OnBuild", "Labels", "StopSignal",
        "StopTimeout", "Shell", "HostConfig", "NetworkingConfig",
    };

    /// <summary>
    /// The only security options a runner sets. Every other one (a seccomp or AppArmor profile,
    /// an SELinux label, unconfined system paths, writable cgroups…) is a way to loosen the
    /// container, so an allowlist — not a denylist — decides.
    /// </summary>
    private static readonly HashSet<string> NoNewPrivileges = new(StringComparer.OrdinalIgnoreCase)
    {
        "no-new-privileges", "no-new-privileges:true", "no-new-privileges=true",
    };

    private static readonly string[] HostNamespaces = ["PidMode", "IpcMode", "UTSMode", "UsernsMode", "CgroupnsMode", "Cgroup"];

    /// <summary>Host-configuration fields the runner never sets — any value would lift a protection.</summary>
    private static readonly string[] NeverSet =
    [
        "Devices", "DeviceRequests", "DeviceCgroupRules", "VolumesFrom", "Links", "Sysctls", "CgroupParent",
        "VolumeDriver", "ContainerIDFile", "Annotations",
    ];

    /// <summary>
    /// Fields that REPLACE the daemon's protective defaults when present at all — an empty list
    /// unmasks <c>/proc</c> and <c>/sys</c> paths, so even that is refused.
    /// </summary>
    private static readonly string[] NeverPresent = ["MaskedPaths", "ReadonlyPaths"];

    /// <summary>Docker's default capability set (moby <c>caps.DefaultCapabilities</c>): what a container keeps unless it drops it.</summary>
    private static readonly string[] DockerDefaultCapabilities =
    [
        "CHOWN", "DAC_OVERRIDE", "FSETID", "FOWNER", "MKNOD", "NET_RAW", "SETGID", "SETUID", "SETFCAP", "SETPCAP",
        "NET_BIND_SERVICE", "SYS_CHROOT", "KILL", "AUDIT_WRITE",
    ];

    /// <summary>
    /// The endpoint settings a runner sets: aliases. Notably never <c>NetworkID</c> — the daemon
    /// attaches a container to the network that id names, whatever the endpoint's name says.
    /// </summary>
    private static readonly HashSet<string> EndpointFields = new(StringComparer.OrdinalIgnoreCase) { "Aliases" };

    /// <summary>Bind options a runner uses: a host directory is read-only or not; a volume may also skip the copy-up.</summary>
    private static readonly string[] HostBindOptions = ["ro", "rw"];

    private static readonly string[] VolumeBindOptions = ["ro", "rw", "nocopy"];

    public static async Task<GuardDecision> DecideAsync(
        JsonElement body, DockerGuardSettings settings, IDockerObjects objects, CancellationToken ct)
    {
        var policy = new DockerApiPolicy(settings, objects);
        if (body.ValueKind != JsonValueKind.Object)
            return GuardDecision.Deny("a container is created from a JSON object");
        if (body.EnumerateObject().Select(p => p.Name).FirstOrDefault(name => !TopLevelFields.Contains(name)) is { } unknown)
            return GuardDecision.Deny($"'{unknown}' is not part of the container configuration a runner sends");
        if (!policy.OwnedLabels(body, "Labels"))
            return GuardDecision.Deny("a runner's container carries an Auxilia ownership label");
        if (!TryGet(body, "HostConfig", out var host) || host.ValueKind != JsonValueKind.Object)
            return GuardDecision.Deny("a runner's container states its confinement (HostConfig)");

        if (IsTrue(host, "Privileged"))
            return GuardDecision.Deny("never privileged");
        if (IsTrue(host, "OomKillDisable"))
            return GuardDecision.Deny("the memory ceiling stays enforced");
        if (IsTrue(host, "PublishAllPorts"))
            return GuardDecision.Deny("ports are never published on every interface");
        var securityOptions = Strings(host, "SecurityOpt").ToList();
        if (!securityOptions.Any(NoNewPrivileges.Contains))
            return GuardDecision.Deny("no-new-privileges is mandatory");
        if (securityOptions.FirstOrDefault(o => !NoNewPrivileges.Contains(o)) is { } loosening)
            return GuardDecision.Deny($"security option '{loosening}' is not one a runner sets");
        if (DecideCapabilities(host, settings) is { } capabilities)
            return capabilities;
        if (DecideRuntime(host, settings) is { } runtime)
            return runtime;

        foreach (var ns in HostNamespaces)
            if (String(host, ns) is { } mode
                && (string.Equals(mode, "host", StringComparison.OrdinalIgnoreCase)
                    || mode.StartsWith("container:", StringComparison.OrdinalIgnoreCase)))
                return GuardDecision.Deny($"{ns} '{mode}' shares a namespace beyond the container");
        if (NeverSet.FirstOrDefault(field => IsNonEmpty(host, field)) is { } neverSet)
            return GuardDecision.Deny($"{neverSet} is never set for a runner's container");
        if (NeverPresent.FirstOrDefault(field => IsSet(host, field)) is { } replaced)
            return GuardDecision.Deny($"{replaced} would replace the daemon's protective defaults");
        if (TryGet(host, "LogConfig", out var log) && String(log, "Type") is { Length: > 0 } driver
            && driver is not ("json-file" or "local"))
            return GuardDecision.Deny($"log driver '{driver}' would ship the container's output elsewhere");

        if (String(host, "NetworkMode") is { Length: > 0 } networkMode && networkMode is not ("default" or "none")
            && (networkMode == "host" || networkMode.StartsWith("container:", StringComparison.Ordinal)
                || !await policy.MayJoinAsync(networkMode, ct)))
            return GuardDecision.Deny($"network mode '{networkMode}' is not Auxilia's");
        if (TryGet(body, "NetworkingConfig", out var networking) && TryGet(networking, "EndpointsConfig", out var endpoints)
            && endpoints.ValueKind == JsonValueKind.Object)
            foreach (var endpoint in endpoints.EnumerateObject())
            {
                if (EndpointRefusal(endpoint.Value) is { } endpointRefusal)
                    return GuardDecision.Deny(endpointRefusal);
                if (!await policy.MayJoinAsync(endpoint.Name, ct))
                    return GuardDecision.Deny($"network '{endpoint.Name}' is not Auxilia's");
            }

        if (TryGet(host, "PortBindings", out var ports) && ports.ValueKind == JsonValueKind.Object)
            foreach (var port in ports.EnumerateObject())
                if (port.Value.ValueKind == JsonValueKind.Array)
                    foreach (var binding in port.Value.EnumerateArray())
                        // Address literals only: the daemon does not resolve names here, and an
                        // unparsable host address ("localhost") means every interface.
                        if (String(binding, "HostIp") is not ("127.0.0.1" or "::1"))
                            return GuardDecision.Deny("a published port stays on loopback (127.0.0.1 or ::1)");

        if (TryGet(host, "Mounts", out var mounts) && mounts.ValueKind == JsonValueKind.Array)
            foreach (var mount in mounts.EnumerateArray())
                if (await DecideMountAsync(mount, objects, policy, ct) is { Allowed: false } refused)
                    return refused;

        // Binds last: a host directory below a root provisions that root's volume, which only
        // an otherwise admissible container should cause.
        var keptBinds = new List<string>();
        var subpathMounts = new List<JsonObject>();
        foreach (var bind in Strings(host, "Binds"))
        {
            var (source, target, options) = ParseBind(bind);
            if (source is null)
            {
                keptBinds.Add(bind); // anonymous volume
                continue;
            }
            if (IsHostPath(source))
            {
                if (options.FirstOrDefault(o => !HostBindOptions.Contains(o, StringComparer.Ordinal)) is { } hostOption)
                    return GuardDecision.Deny($"bind option '{hostOption}' is not one a runner uses");
                if (!TryBelowRoot(source, settings, out var root, out var subpath))
                    return GuardDecision.Deny($"host path '{source}' is not below one of the runner's roots");
                string rootVolume;
                try
                {
                    rootVolume = await objects.RootVolumeAsync(root, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return GuardDecision.Deny($"host directories below '{root}' cannot be bound safely: {ex.Message}");
                }
                subpathMounts.Add(SubpathMount(rootVolume, subpath, target!, readOnly: options.Contains("ro")));
                continue;
            }
            if (options.FirstOrDefault(o => !VolumeBindOptions.Contains(o, StringComparer.Ordinal)) is { } volumeOption)
                return GuardDecision.Deny($"bind option '{volumeOption}' is not one a runner uses");
            if (!policy.IsOwned(await objects.VolumeLabelsAsync(source, ct)))
                return GuardDecision.Deny($"volume '{source}' is not Auxilia's");
            keptBinds.Add(bind);
        }

        return subpathMounts.Count == 0
            ? GuardDecision.Allow
            : GuardDecision.AllowAs(Rewrite(body, keptBinds, subpathMounts));
    }

    /// <summary>
    /// The capabilities the container would end up with — Docker's defaults minus <c>CapDrop</c>
    /// plus <c>CapAdd</c>, both read as the daemon reads them (a bare string is a list) — must stay
    /// within <see cref="DockerGuardSettings.AllowedCapabilities"/>.
    /// </summary>
    private static GuardDecision? DecideCapabilities(JsonElement host, DockerGuardSettings settings)
    {
        if (StringSlice(host, "CapAdd") is not { } capAdd || StringSlice(host, "CapDrop") is not { } capDrop)
            return GuardDecision.Deny("capabilities are listed by name");
        var added = capAdd.Select(Capability).ToHashSet(StringComparer.Ordinal);
        var dropped = capDrop.Select(Capability).ToHashSet(StringComparer.Ordinal);
        if (added.Contains("ALL"))
            return GuardDecision.Deny("capability ALL is not one a runner's container keeps");
        var kept = dropped.Contains("ALL") ? [] : DockerDefaultCapabilities.Where(c => !dropped.Contains(c));
        var permitted = settings.AllowedCapabilities.Select(Capability).ToHashSet(StringComparer.Ordinal);
        return kept.Concat(added).FirstOrDefault(c => !permitted.Contains(c)) is { } extra
            ? GuardDecision.Deny($"capability {extra} is not one a runner's container keeps")
            : null;
    }

    /// <summary>The OCI runtime: the daemon's default, or — when the guard lists runtimes — one of those, by name.</summary>
    private static GuardDecision? DecideRuntime(JsonElement host, DockerGuardSettings settings)
    {
        var runtime = String(host, "Runtime");
        if (settings.Runtimes.Length == 0)
            return string.IsNullOrEmpty(runtime)
                ? null
                : GuardDecision.Deny($"runtime '{runtime}' is not the daemon's default (the guard lists no runtimes)");
        return runtime is not null && settings.Runtimes.Contains(runtime, StringComparer.Ordinal)
            ? null
            : GuardDecision.Deny($"a runner's container runs under {string.Join(" or ", settings.Runtimes)}"
                                 + (string.IsNullOrEmpty(runtime) ? "" : $", not '{runtime}'"));
    }

    /// <summary>Why an endpoint's settings are refused (null = they pass): only the fields a runner sets.</summary>
    internal static string? EndpointRefusal(JsonElement endpoint) => endpoint.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Object => endpoint.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !EndpointFields.Contains(n))
            is { } field
            ? $"endpoint setting '{field}' is not one a runner sets"
            : null,
        _ => "endpoint settings are an object",
    };

    /// <summary>
    /// A host bind carried out as a subpath mount of the root's volume: the daemon opens every
    /// component beneath the root without following a symlink out of it and mounts exactly what
    /// it validated, so a symlink planted in a run directory cannot point the mount at the host.
    /// No copy-up — like the bind it replaces, the directory shows only its own content.
    /// </summary>
    private static JsonObject SubpathMount(string rootVolume, string subpath, string target, bool readOnly) => new()
    {
        ["Type"] = "volume",
        ["Source"] = rootVolume,
        ["Target"] = target,
        ["ReadOnly"] = readOnly,
        ["VolumeOptions"] = new JsonObject { ["NoCopy"] = true, ["Subpath"] = subpath },
    };

    /// <summary>The caller's body with its host binds replaced by <paramref name="subpathMounts"/>.</summary>
    private static byte[] Rewrite(JsonElement body, List<string> keptBinds, List<JsonObject> subpathMounts)
    {
        var root = JsonNode.Parse(body.GetRawText())!.AsObject();
        var host = root[KeyOf(root, "HostConfig")!]!.AsObject();
        var bindsKey = KeyOf(host, "Binds") ?? "Binds";
        if (keptBinds.Count > 0)
            host[bindsKey] = new JsonArray(keptBinds.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray());
        else
            host.Remove(bindsKey);
        var mountsKey = KeyOf(host, "Mounts") ?? "Mounts";
        if (host[mountsKey] is not JsonArray mountList)
            host[mountsKey] = mountList = [];
        foreach (var mount in subpathMounts)
            mountList.Add(mount);
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    /// <summary>The property's name as the body spells it (the daemon reads names case-insensitively).</summary>
    private static string? KeyOf(JsonObject node, string name)
        => node.Select(p => p.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>"[source:]target[:options]"</c> as the daemon's Linux parser splits it; a Windows host
    /// path keeps its drive colon (refused below, but never misread as a volume name).
    /// </summary>
    private static (string? Source, string? Target, string[] Options) ParseBind(string bind)
    {
        var start = DrivePath.IsMatch(bind) ? 2 : 0;
        var colon = bind.IndexOf(':', start);
        if (colon < 0)
            return (null, bind, []);
        var rest = bind[(colon + 1)..].Split(':', 2);
        var options = rest.Length > 1 ? rest[1].Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
        return (bind[..colon], rest[0], options);
    }

    private static async Task<GuardDecision> DecideMountAsync(
        JsonElement mount, IDockerObjects objects, DockerApiPolicy policy, CancellationToken ct)
    {
        var type = String(mount, "Type") ?? "volume";
        var source = String(mount, "Source");
        if (TryGet(mount, "BindOptions", out var bindOptions) && IsNonEmpty(bindOptions, "Propagation"))
            return GuardDecision.Deny("bind propagation would carry mounts across the container boundary");
        if (TryGet(mount, "VolumeOptions", out var volumeOptions))
        {
            // A driver configuration — even the local driver's, without a name — can mount any
            // host directory ("o=bind,device=/").
            if (IsSet(volumeOptions, "DriverConfig"))
                return GuardDecision.Deny("a runner's volumes use the daemon's local driver without options");
            if (IsNonEmpty(volumeOptions, "Subpath"))
                return GuardDecision.Deny("a runner never mounts part of a volume");
        }
        return type switch
        {
            "tmpfs" => GuardDecision.Allow,
            "volume" when string.IsNullOrEmpty(source) => GuardDecision.Allow,
            "volume" => policy.IsOwned(await objects.VolumeLabelsAsync(source!, ct))
                ? GuardDecision.Allow
                : GuardDecision.Deny($"volume '{source}' is not Auxilia's"),
            // Host directories go through Binds, which the guard carries out beneath their root.
            _ => GuardDecision.Deny($"mount type '{type}' is never used by the runner"),
        };
    }

    private static bool IsHostPath(string source)
        => source.StartsWith('/') || source.StartsWith('\\') || DrivePath.IsMatch(source);

    /// <summary>
    /// Whether <paramref name="path"/> lies strictly below one of the configured roots (absolute
    /// POSIX paths, no traversal); <paramref name="subpath"/> is the part below the root.
    /// </summary>
    internal static bool TryBelowRoot(string path, DockerGuardSettings settings, out string root, out string subpath)
    {
        root = subpath = "";
        if (Normalize(path) is not { } normalized)
            return false;
        foreach (var candidate in settings.BindRoots)
        {
            if (Normalize(candidate) is not { Length: > 0 } normalizedRoot || !normalizedRoot.StartsWith('/'))
                continue;
            if (normalized.StartsWith(normalizedRoot + "/", StringComparison.Ordinal))
            {
                root = normalizedRoot;
                subpath = normalized[(normalizedRoot.Length + 1)..];
                return true;
            }
        }
        return false;
    }

    private static string? Normalize(string path)
    {
        if (!path.StartsWith('/') || path.Contains('\\'))
            return null;
        if (path.Split('/').Any(segment => segment is ".." or "."))
            return null;
        return Regex.Replace(path, "/{2,}", "/").TrimEnd('/');
    }

    private static string Capability(string name)
    {
        var upper = name.Trim().ToUpperInvariant();
        return upper.StartsWith("CAP_", StringComparison.Ordinal) ? upper[4..] : upper;
    }
}

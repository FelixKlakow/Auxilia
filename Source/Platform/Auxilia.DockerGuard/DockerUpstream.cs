using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Auxilia.DockerGuard;

/// <summary>
/// The real Docker daemon behind the guard — over its unix socket, named pipe or TCP endpoint.
/// Carries the forwarded calls and answers the ownership lookups of the policy (the labels of
/// existing containers, networks and volumes, as the daemon reports them) and keeps the root
/// volumes host binds are carried out on.
/// </summary>
public sealed class DockerUpstream : IDockerObjects, IDisposable
{
    /// <summary>Marks a volume as the guard's own root volume; its value is the root it binds.</summary>
    public const string RootVolumeLabel = "auxilia.guard-root";

    /// <summary>Volume subpath mounts — what makes a guarded host bind safe — need Engine API 1.45 (Docker 26).</summary>
    private static readonly Version SubpathMounts = new(1, 45);

    private readonly HttpClient _http;
    private readonly DockerGuardSettings _settings;
    private readonly SemaphoreSlim _rootVolumeGate = new(1, 1);
    private bool _subpathMountsVerified;

    public DockerUpstream(DockerGuardSettings settings)
    {
        _settings = settings;
        var upstream = new Uri(settings.Upstream);
        var handler = new SocketsHttpHandler
        {
            // Long-polls (wait), log streams and builds outlive any fixed timeout.
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            UseProxy = false,
        };
        switch (upstream.Scheme)
        {
            case "unix":
                var socketPath = upstream.AbsolutePath;
                handler.ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                };
                BaseAddress = new Uri("http://docker/");
                break;
            case "npipe":
                var pipeName = upstream.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
                handler.ConnectCallback = async (_, ct) =>
                {
                    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(ct);
                    return pipe;
                };
                BaseAddress = new Uri("http://docker/");
                break;
            case "tcp" or "http":
                BaseAddress = new Uri($"http://{upstream.Authority}/");
                break;
            default:
                throw new InvalidOperationException($"unsupported Docker upstream '{settings.Upstream}'");
        }
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, BaseAddress = BaseAddress };
    }

    public Uri BaseAddress { get; }

    /// <summary>Forwards one request; the response is returned once its headers arrived (the body streams).</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

    public async Task<IReadOnlyDictionary<string, string>?> ContainerLabelsAsync(string id, CancellationToken ct)
        => await GetJsonAsync($"containers/{Uri.EscapeDataString(id)}/json", ct) is { } container
           && JsonBody.TryGet(container, "Config", out var config)
            ? JsonBody.StringMap(config, "Labels") ?? new Dictionary<string, string>()
            : null;

    public async Task<DockerNetwork?> NetworkAsync(string idOrName, CancellationToken ct)
        => await GetJsonAsync($"networks/{Uri.EscapeDataString(idOrName)}", ct) is { } network
            ? new DockerNetwork(
                JsonBody.String(network, "Id") ?? idOrName,
                JsonBody.String(network, "Name") ?? idOrName,
                JsonBody.StringMap(network, "Labels") ?? new Dictionary<string, string>())
            : null;

    public async Task<IReadOnlyDictionary<string, string>?> VolumeLabelsAsync(string name, CancellationToken ct)
        => await GetJsonAsync($"volumes/{Uri.EscapeDataString(name)}", ct) is { } volume
            ? JsonBody.StringMap(volume, "Labels") ?? new Dictionary<string, string>()
            : null;

    public async Task<string> RootVolumeAsync(string root, CancellationToken ct)
    {
        // Serialized: two creates racing for the same root provision its volume once. The
        // volume is re-verified on every use — an operator may have removed or replaced it.
        await _rootVolumeGate.WaitAsync(ct);
        try
        {
            await VerifySubpathMountsAsync(ct);
            var name = RootVolumeName(root);
            var volume = await GetJsonAsync($"volumes/{name}", ct);
            if (volume is null)
            {
                var labels = new Dictionary<string, string>(_settings.RootVolumeLabels) { [RootVolumeLabel] = root };
                using var created = await _http.PostAsync("volumes/create", JsonContent.Create(new Dictionary<string, object>
                {
                    ["Name"] = name,
                    ["Driver"] = "local",
                    ["DriverOpts"] = new Dictionary<string, string> { ["type"] = "none", ["o"] = "bind", ["device"] = root },
                    ["Labels"] = labels,
                }), ct);
                if (!created.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"the root volume '{name}' could not be created: {await created.Content.ReadAsStringAsync(ct)}");
                volume = await GetJsonAsync($"volumes/{name}", ct);
            }
            if (volume is not { } existing || !BindsRoot(existing, root))
                throw new InvalidOperationException($"volume '{name}' exists but does not bind '{root}'");
            return name;
        }
        finally
        {
            _rootVolumeGate.Release();
        }
    }

    /// <summary>A root's volume name: stable per root, distinct across roots.</summary>
    public static string RootVolumeName(string root)
        => "auxilia-guard-root-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16];

    private static bool BindsRoot(JsonElement volume, string root)
        => JsonBody.String(volume, "Driver") == "local"
           && JsonBody.StringMap(volume, "Options") is { } options
           && options.GetValueOrDefault("device") == root
           && options.GetValueOrDefault("type") == "none"
           && (options.GetValueOrDefault("o") ?? "").Split(',').Contains("bind");

    /// <summary>
    /// An older daemon would IGNORE the subpath and mount the whole root — so without subpath
    /// support no host bind is carried out at all.
    /// </summary>
    private async Task VerifySubpathMountsAsync(CancellationToken ct)
    {
        if (_subpathMountsVerified)
            return;
        var api = await GetJsonAsync("version", ct) is { } version ? JsonBody.String(version, "ApiVersion") : null;
        if (api is null || !DockerApiPolicy.TryParseVersion(api, out var parsed) || parsed < SubpathMounts)
            throw new InvalidOperationException(
                $"the Docker daemon (Engine API {api ?? "unknown"}) cannot mount volume subpaths — guarded host binds need Docker 26 (API 1.45) or newer");
        _subpathMountsVerified = true;
    }

    private async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        _http.Dispose();
        _rootVolumeGate.Dispose();
    }
}

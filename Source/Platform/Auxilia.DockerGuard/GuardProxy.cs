using System.Text;
using System.Text.Json;

namespace Auxilia.DockerGuard;

/// <summary>One JSON object per line on stdout for every refusal and every allowed mutation.</summary>
public sealed class GuardLog(TextWriter output)
{
    private readonly object _gate = new();

    public void Write(string decision, string method, string path, string? reason = null)
    {
        var line = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["decision"] = decision,
            ["method"] = method,
            ["path"] = path,
            ["reason"] = reason,
        }.Where(kv => kv.Value is not null).ToDictionary());
        lock (_gate)
        {
            output.WriteLine(line);
            output.Flush();
        }
    }
}

/// <summary>
/// The guard's request pipeline: decides every Docker API call with <see cref="DockerApiPolicy"/>
/// and forwards only what it allows — bodies and responses stream through unchanged (log
/// streams, waits and builds keep flowing), except a create whose host binds the policy carries
/// out beneath their root (its rewritten body is forwarded); a refusal answers 403 with a
/// Docker-style <c>{"message": …}</c>. Connection upgrades (attach, exec) are never forwarded.
/// </summary>
public sealed class GuardProxy(DockerApiPolicy policy, DockerUpstream upstream, DockerGuardSettings settings, GuardLog log)
{
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host",
        "Content-Length",
    };

    public async Task HandleAsync(HttpContext http)
    {
        var request = http.Request;
        var method = request.Method;
        var path = request.Path.Value ?? "/";
        var ct = http.RequestAborted;

        if (request.Headers.ContainsKey("Upgrade"))
        {
            await RefuseAsync(http, method, path, "connection upgrades (attach, exec) are never forwarded");
            return;
        }

        byte[]? buffered = null;
        JsonElement? body = null;
        if (DockerApiPolicy.NeedsBody(method, path))
        {
            buffered = await ReadBoundedAsync(request.Body, settings.MaxInspectedBodyBytes, ct);
            if (buffered is null)
            {
                await RefuseAsync(http, method, path, "the request body exceeds the inspection limit");
                return;
            }
            try
            {
                using var document = JsonDocument.Parse(buffered.Length == 0 ? "{}"u8.ToArray() : buffered);
                body = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                await RefuseAsync(http, method, path, "the request body is not JSON");
                return;
            }
        }

        // Parsed the way the daemon parses it — the decision must be about what the daemon reads.
        var query = DockerApiPolicy.ParseQuery(request.QueryString.Value);
        var decision = await policy.DecideAsync(method, path, query, body, ct);
        if (!decision.Allowed)
        {
            await RefuseAsync(http, method, path, decision.Reason!);
            return;
        }
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
            log.Write("allow", method, path, decision.Body is null ? null : "host binds carried out beneath their roots");

        using var forward = new HttpRequestMessage(
            new HttpMethod(method),
            new Uri(upstream.BaseAddress, request.Path.ToUriComponent() + request.QueryString.ToUriComponent()));
        if (decision.Body is { } rewritten)
        {
            forward.Content = new ByteArrayContent(rewritten);
            forward.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        else if (buffered is not null)
            forward.Content = new ByteArrayContent(buffered);
        else if (request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            forward.Content = new StreamContent(request.Body);
            if (request.ContentLength is { } length)
                forward.Content.Headers.ContentLength = length;
        }
        foreach (var (name, values) in request.Headers)
        {
            if (HopByHop.Contains(name)
                || (decision.Body is not null && name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!forward.Headers.TryAddWithoutValidation(name, values.ToArray()))
                forward.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
        }

        using var response = await upstream.SendAsync(forward, ct);
        http.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            if (!HopByHop.Contains(name))
                http.Response.Headers[name] = values.ToArray();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            await http.Response.Body.WriteAsync(chunk.AsMemory(0, read), ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }

    private async Task RefuseAsync(HttpContext http, string method, string path, string reason)
    {
        log.Write("deny", method, path, reason);
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(
            JsonSerializer.Serialize(new { message = $"auxilia docker guard: {reason}" }), Encoding.UTF8);
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream body, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
                return null;
        }
        return buffer.ToArray();
    }
}

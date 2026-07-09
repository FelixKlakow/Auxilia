using System.Collections.Concurrent;
using System.Text.Json;

namespace Auxilia.BackendService.Dashboard.Connect;

/// <summary>GitHub device-flow coordinates; overridable via the "Connect:GitHub" config section.</summary>
public sealed class GitHubConnectSettings
{
    /// <summary>The GitHub CLI's public OAuth client — device flow works without a secret.</summary>
    public string ClientId { get; set; } = "178c6fc778ccc68e1d6a";

    public string DeviceCodeEndpoint { get; set; } = "https://github.com/login/device/code";
    public string TokenEndpoint { get; set; } = "https://github.com/login/oauth/access_token";
    public string Scopes { get; set; } = "repo";
}

/// <summary>
/// "Connect GitHub": the device flow — Begin fetches a short user code, the user enters it
/// on github.com/login/device and approves, Complete polls the token endpoint. Not approved
/// yet surfaces as <see cref="ConnectPendingException"/> so finishing is simply retried.
/// The token is returned to the caller — never stored or logged here.
/// </summary>
public sealed class GitHubConnectFlow(
    GitHubConnectSettings settings,
    HttpClient? httpClient = null,
    TimeProvider? timeProvider = null) : IConnectFlow
{
    public const string FlowKey = "github";

    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);

    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Pending device codes by state; entries expire with GitHub's own code lifetime.</summary>
    private readonly ConcurrentDictionary<string, (string DeviceCode, DateTimeOffset StartedUtc)> _pending = new();

    public string Key => FlowKey;
    public string DisplayName => "GitHub";
    public string Instructions
        => "Enter the code on the GitHub page and approve, then finish here.";

    public async Task<ConnectStart> BeginAsync(CancellationToken ct = default)
    {
        Evict();
        using var response = await _httpClient.SendAsync(DeviceCodeRequest(), ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GitHub did not hand out a device code (HTTP {(int)response.StatusCode}).");

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var deviceCode = payload.RootElement.GetProperty("device_code").GetString()!;
        var userCode = payload.RootElement.GetProperty("user_code").GetString()!;
        var verificationUri = payload.RootElement.TryGetProperty("verification_uri", out var uri)
            ? uri.GetString()! : "https://github.com/login/device";

        var state = Guid.NewGuid().ToString("N");
        _pending[state] = (deviceCode, _time.GetUtcNow());
        return new ConnectStart(verificationUri, state, UserCode: userCode);
    }

    public async Task<string> CompleteAsync(string state, string? pastedCode, CancellationToken ct = default)
    {
        Evict();
        // Consumed only on success — pending approvals are retried with the same state.
        if (!_pending.TryGetValue(state, out var pending))
            throw new InvalidOperationException("This connect attempt expired — start again with Connect.");

        using var response = await _httpClient.SendAsync(TokenRequest(pending.DeviceCode), ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"The sign-in could not be completed (HTTP {(int)response.StatusCode}) — start again with Connect.");

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (payload.RootElement.TryGetProperty("access_token", out var token)
            && token.GetString() is { Length: > 0 } accessToken)
        {
            _pending.TryRemove(state, out _);
            return accessToken;
        }

        var error = payload.RootElement.TryGetProperty("error", out var errorCode)
            ? errorCode.GetString()
            : null;
        throw error switch
        {
            "authorization_pending" or "slow_down" => new ConnectPendingException(
                "GitHub has not seen the approval yet — approve on the GitHub page, then finish again."),
            "expired_token" => new InvalidOperationException(
                "The GitHub code expired — start again with Connect."),
            "access_denied" => new InvalidOperationException("The sign-in was denied on GitHub."),
            _ => new InvalidOperationException(
                $"The sign-in could not be completed ({error ?? "no token in the response"}).")
        };
    }

    private HttpRequestMessage DeviceCodeRequest()
        => JsonRequest(settings.DeviceCodeEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["scope"] = settings.Scopes
        });

    private HttpRequestMessage TokenRequest(string deviceCode)
        => JsonRequest(settings.TokenEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["device_code"] = deviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
        });

    private static HttpRequestMessage JsonRequest(string endpoint, Dictionary<string, string> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        // GitHub answers form-encoded unless JSON is asked for explicitly.
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private void Evict()
    {
        var cutoff = _time.GetUtcNow() - StateLifetime;
        foreach (var (state, pending) in _pending)
        {
            if (pending.StartedUtc < cutoff)
                _pending.TryRemove(state, out _);
        }
    }
}

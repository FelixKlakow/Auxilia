using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Auxilia.BackendService.Dashboard.Connect;

/// <summary>Endpoints and client coordinates of the Claude sign-in; overridable via the "Connect:AnthropicClaude" config section.</summary>
public sealed class AnthropicConnectSettings
{
    /// <summary>The Claude Code CLI's public OAuth client — the same one 'claude setup-token' uses.</summary>
    public string ClientId { get; set; } = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    public string AuthorizeEndpoint { get; set; } = "https://claude.ai/oauth/authorize";
    public string TokenEndpoint { get; set; } = "https://console.anthropic.com/v1/oauth/token";

    /// <summary>The code-display callback: the user copies the shown code back instead of being redirected.</summary>
    public string RedirectUri { get; set; } = "https://console.anthropic.com/oauth/code/callback";

    public string Scopes { get; set; } = "org:create_api_key user:profile user:inference";
}

/// <summary>
/// "Connect Claude account": the PKCE sign-in the Claude Code CLI's own 'setup-token' uses.
/// Begin hands out the claude.ai authorize URL; the user approves and pastes the shown code
/// back; Complete exchanges it for the account token (the CLI's CLAUDE_CODE_OAUTH_TOKEN).
/// The token is returned to the caller — this class never stores or logs it.
/// </summary>
public sealed class AnthropicClaudeConnectFlow(
    AnthropicConnectSettings settings,
    HttpClient? httpClient = null,
    TimeProvider? timeProvider = null) : IConnectFlow
{
    public const string FlowKey = "anthropic-claude";

    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Pending PKCE verifiers by state; entries expire so abandoned attempts don't accumulate.</summary>
    private readonly ConcurrentDictionary<string, (string Verifier, DateTimeOffset StartedUtc)> _pending = new();

    public string Key => FlowKey;
    public string DisplayName => "Claude account";
    public string Instructions
        => "Sign in and approve on the Claude page, then paste the code it shows you back here.";

    public Task<ConnectStart> BeginAsync(CancellationToken ct = default)
    {
        Evict();
        var verifier = RandomToken();
        var state = RandomToken();
        _pending[state] = (verifier, _time.GetUtcNow());

        var url = settings.AuthorizeEndpoint
                  + "?code=true"
                  + "&client_id=" + Uri.EscapeDataString(settings.ClientId)
                  + "&response_type=code"
                  + "&redirect_uri=" + Uri.EscapeDataString(settings.RedirectUri)
                  + "&scope=" + Uri.EscapeDataString(settings.Scopes)
                  + "&code_challenge=" + Challenge(verifier)
                  + "&code_challenge_method=S256"
                  + "&state=" + Uri.EscapeDataString(state);
        return Task.FromResult(new ConnectStart(url, state, PasteRequired: true));
    }

    public async Task<string> CompleteAsync(string state, string? pastedCode, CancellationToken ct = default)
    {
        Evict();
        // Consumed only on success so a mistyped paste can simply be corrected.
        if (!_pending.TryGetValue(state, out var pending))
            throw new InvalidOperationException("This connect attempt expired — start again with Connect.");

        // The callback page shows "code#state"; accept the pure code too.
        var code = pastedCode?.Trim() ?? "";
        var separator = code.IndexOf('#');
        if (separator >= 0)
            code = code[..separator];
        if (code.Length == 0)
            throw new ConnectPendingException("Paste the code shown after approving the sign-in.");

        using var response = await _httpClient.PostAsync(settings.TokenEndpoint,
            new StringContent(JsonSerializer.Serialize(new
            {
                grant_type = "authorization_code",
                code,
                state,
                client_id = settings.ClientId,
                redirect_uri = settings.RedirectUri,
                code_verifier = pending.Verifier
            }), Encoding.UTF8, "application/json"), ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"The sign-in could not be completed (HTTP {(int)response.StatusCode}) — start again with Connect.");

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!payload.RootElement.TryGetProperty("access_token", out var token)
            || token.GetString() is not { Length: > 0 } accessToken)
            throw new InvalidOperationException("The sign-in response carried no token — start again with Connect.");
        _pending.TryRemove(state, out _);
        return accessToken;
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

    private static string RandomToken()
        => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Challenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

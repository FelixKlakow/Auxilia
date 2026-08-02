using Auxilia.Core.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Auxilia.Core.Api.Auth;

/// <summary>
/// Wires the Core's authentication authority: the bearer API-key scheme (programmatic default for
/// REST + MCP), a browser session cookie for interactive sign-in, and — when configured — the Entra
/// OIDC relying-party scheme. Every scheme resolves to the same <c>auxilia:principal-id</c> claim, so
/// the Policy Engine authorizes cookie and API-key callers identically.
/// </summary>
public static class CoreAuthExtensions
{
    /// <summary>Authorization policy that accepts ONLY an interactive session cookie (for <c>POST /auth/token</c>).</summary>
    public const string CookieSessionPolicy = "CookieSession";

    public static IServiceCollection AddCoreAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OidcSettings>(configuration.GetSection("Oidc"));
        var oidc = configuration.GetSection("Oidc").Get<OidcSettings>() ?? new OidcSettings();

        var auth = services.AddAuthentication(options =>
        {
            // API/MCP callers are the default: no credentials → a 401, never a browser redirect.
            options.DefaultScheme = AuthSchemes.ApiKey;
            options.DefaultChallengeScheme = AuthSchemes.ApiKey;
        });

        auth.AddScheme<AuthenticationSchemeOptions, CoreApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, null);

        // Per-user bearer: a delegated console calling AS the signed-in user. A distinct token shape
        // (auxu_…) and scheme from the API key, minted by POST /auth/token from a live cookie session.
        services.AddSingleton<UserBearerTokenService>();
        auth.AddScheme<AuthenticationSchemeOptions, CoreUserBearerAuthenticationHandler>(AuthSchemes.UserBearer, null);

        auth.AddCookie(AuthSchemes.Cookie, options =>
        {
            options.Cookie.Name = "auxilia.core.session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            // This is an API host, not an MVC app: surface auth failures as status codes.
            options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });

        // Group-claim overage fallback: read >~200-group memberships from Microsoft Graph at sign-in.
        // Wired to Graph only when interactive sign-in is configured; otherwise a harmless no-op.
        if (oidc.Enabled)
        {
            services.AddHttpClient();
            services.AddSingleton<IDirectoryGroupResolver, GraphDirectoryGroupResolver>();
        }
        else
        {
            services.AddSingleton<IDirectoryGroupResolver, NullDirectoryGroupResolver>();
        }

        // OBO delegation: real Entra on-behalf-of exchange when delegation is enabled, else a no-op
        // so a delegated slot simply fails closed.
        if (oidc.Enabled && oidc.EnableDelegation)
            services.AddSingleton<IDelegatedTokenExchange, EntraOboTokenExchange>();
        else
            services.AddSingleton<IDelegatedTokenExchange, NullDelegatedTokenExchange>();

        if (oidc.Enabled)
        {
            // Short-lived cookie that carries the validated external identity from the OIDC
            // callback to /auth/callback, where it is exchanged for a provisioned principal.
            auth.AddCookie(AuthSchemes.ExternalCookie, options =>
            {
                options.Cookie.Name = "auxilia.core.external";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.None;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            });

            auth.AddOpenIdConnect(AuthSchemes.Oidc, options =>
            {
                options.Authority = oidc.Authority;
                options.ClientId = oidc.ClientId;
                options.ClientSecret = oidc.ClientSecret;
                options.CallbackPath = oidc.CallbackPath;
                options.SignInScheme = AuthSchemes.ExternalCookie;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false; // keep raw claim names (oid, sub, groups, name)
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                foreach (var scope in oidc.Scopes)
                    options.Scope.Add(scope);
            });
        }

        // Protected endpoints accept the API key, a per-user bearer, or a session cookie; the resolved
        // principal is the same either way. No credentials → the default (API-key) challenge → 401.
        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(
                new AuthorizationPolicyBuilder(AuthSchemes.ApiKey, AuthSchemes.UserBearer, AuthSchemes.Cookie)
                    .RequireAuthenticatedUser()
                    .Build())
            // A per-user bearer is minted ONLY from an interactive cookie session — never from an API
            // key or another bearer — so a service principal cannot self-issue a user-scoped token.
            .AddPolicy(CookieSessionPolicy, policy => policy
                .AddAuthenticationSchemes(AuthSchemes.Cookie)
                .RequireAuthenticatedUser());

        return services;
    }
}

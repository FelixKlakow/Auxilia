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

        // Protected endpoints accept either the API key or a session cookie; the resolved principal
        // is the same either way. No credentials → the default (API-key) challenge → 401.
        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder(AuthSchemes.ApiKey, AuthSchemes.Cookie)
                .RequireAuthenticatedUser()
                .Build());

        return services;
    }
}

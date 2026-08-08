using Auxilia.AdminConsole.Auth;
using Auxilia.AdminConsole.Components;
using Auxilia.AdminConsole.Rendering;
using Auxilia.AdminConsole.Support;
using Auxilia.Core.Client;
using Auxilia.Workflows.Views;
using BlazorAgentView.Services;
using Microsoft.AspNetCore.Components.Authorization;

var builder = WebApplication.CreateBuilder(args);

// --- Core client configuration ---
// The console is a PURE Core.Client consumer: it talks only to Core.Api. Deployment note
// (see docs/backend-service-retirement-plan.md): the console and Core.Api are served SAME-ORIGIN
// behind one gateway, so the browser's "auxilia.core.session" cookie reaches the console and the
// console mints a per-user bearer via Core POST /auth/token. BaseAddress therefore normally points
// at the gateway/Core origin; ApiKey (optional) is the service fallback for non-delegated calls.
var coreOptions = new CoreClientOptions();
builder.Configuration.GetSection("Core").Bind(coreOptions);

builder.Services.AddHttpContextAccessor();

// Per-user bearer handoff: capture the operator's Core session cookie during prerender, exchange it for
// a short-lived user token, and relay that token across the prerender → interactive-circuit boundary so
// the delegated identity survives the whole circuit (see ConsoleCallerTokenProvider / IUserBearerRelay).
// The token itself stays server-side (singleton handle store); the page only carries a one-shot handle.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<UserBearerHandleStore>();
builder.Services.AddScoped<IUserBearerRelay, PersistentUserBearerRelay>();
builder.Services.AddScoped<ConsoleCallerTokenProvider>();
builder.Services.AddScoped<ICoreCallerTokenProvider>(sp => sp.GetRequiredService<ConsoleCallerTokenProvider>());

// Dedicated client for the cookie→bearer exchange (no bearer handler — avoids recursion).
builder.Services.AddHttpClient(ConsoleCallerTokenProvider.CoreAuthHttpClientName, http =>
{
    if (!string.IsNullOrEmpty(coreOptions.BaseAddress))
        http.BaseAddress = new Uri(coreOptions.BaseAddress);
});

// Pooled primary handler for the Core client (connection reuse); it carries NO auth header — the
// per-request bearer is attached by a per-scope CoreCallerTokenHandler below.
const string corePrimaryClientName = "core-primary";
builder.Services.AddHttpClient(corePrimaryClientName, http =>
{
    if (!string.IsNullOrEmpty(coreOptions.BaseAddress))
        http.BaseAddress = new Uri(coreOptions.BaseAddress);
});

// ICoreClient is composed PER SCOPE (not via AddCoreClient's pooled HttpMessageHandler scope, which is
// long-lived and shared across circuits): the CoreCallerTokenHandler resolves THIS circuit's token
// provider, chained in front of the pooled primary handler. So every ICoreClient call carries the
// signed-in user's bearer during interactive rendering, falling back to the static app key when there is
// no live session (e.g. background/health checks).
builder.Services.AddScoped<ICoreClient>(sp =>
{
    var primary = sp.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(corePrimaryClientName);
    var handler = new CoreCallerTokenHandler(sp.GetRequiredService<ICoreCallerTokenProvider>(), coreOptions.ApiKey)
    {
        InnerHandler = primary
    };
    var http = new HttpClient(handler, disposeHandler: false)
    {
        BaseAddress = new Uri(coreOptions.BaseAddress)
    };
    return CoreClientExtensions.Create(http);
});

// --- Authentication state derived from the Core (no local identity store) ---
// The circuit-level provider AND the endpoint-level scheme both ask the Core who the caller
// is; without the scheme, the initial HTTP request of any [Authorize] page has no
// IAuthenticationService and throws before Blazor ever renders.
builder.Services.AddSingleton(coreOptions);
builder.Services.AddScoped<AuthenticationStateProvider, ConsoleAuthenticationStateProvider>();
builder.Services.AddAuthentication(CoreBackedAuthenticationHandler.Scheme)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, CoreBackedAuthenticationHandler>(
        CoreBackedAuthenticationHandler.Scheme, null);
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// --- Blazor UI (interactive server) + the workflow view renderers (moved from BackendService) ---
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
// Ambient shell state (sidebar live-run count + Core health): one poll loop per circuit.
builder.Services.AddScoped<LiveRunMonitor>();
builder.Services.AddScoped<StepUpFlow>();
builder.Services.AddScoped<SharingDirectory>();
builder.Services.AddSingleton<ViewRendererRegistry>();
builder.Services.AddBlazorAgentView();
builder.Services.AddViewRenderer<AgentChatRenderer>(AgentChatEntry.RendererKey);

var app = builder.Build();

// Fingerprinted static assets (resolved via @Assets[...]) — a changed stylesheet gets a new
// URL, so browser caches can never serve a stale one.
app.MapStaticAssets();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

// Make the implicit Program class visible to test projects
public partial class Program
{
}

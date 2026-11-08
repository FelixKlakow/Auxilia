using Auxilia.DockerGuard;

// The runner's only door to the Docker daemon (ARCHITECTURE §7, "Kernel / host"): it holds the
// daemon socket, the runner holds this guard's endpoint. Configuration (section "DockerGuard",
// e.g. DockerGuard__BindRoots__0=/var/lib/auxilia/workspaces): see DockerGuardSettings.
// Listen address: ASPNETCORE_URLS — a private network ("http://+:2375") or a unix socket
// ("http://unix:/run/auxilia/docker-guard.sock") shared with the runner only.
var app = GuardHost.Build(args);
app.Run();

/// <summary>Builds the guard's web host — also used in-process by the tests.</summary>
public static class GuardHost
{
    public static WebApplication Build(string[] args, Action<DockerGuardSettings>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var settings = builder.Configuration.GetSection("DockerGuard").Get<DockerGuardSettings>() ?? new DockerGuardSettings();
        configure?.Invoke(settings);
        // Build contexts and archive uploads stream through; the policy never needs their bytes.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = null);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<DockerUpstream>();
        builder.Services.AddSingleton<IDockerObjects>(sp => sp.GetRequiredService<DockerUpstream>());
        builder.Services.AddSingleton<DockerApiPolicy>();
        builder.Services.AddSingleton(new GuardLog(Console.Out));
        builder.Services.AddSingleton<GuardProxy>();
        var app = builder.Build();
        var proxy = app.Services.GetRequiredService<GuardProxy>();
        app.Run(proxy.HandleAsync);
        return app;
    }
}

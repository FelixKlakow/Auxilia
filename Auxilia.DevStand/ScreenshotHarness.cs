using Auxilia.PlatformData.Entities;
using Auxilia.SystemTestSuite.EndToEnd;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace Auxilia.DevStand;

/// <summary>
/// Visual verification harness: boots the same containerized stack as the interactive dev
/// stand, triggers one Code Review run via demo mail, waits for it to finish, and captures
/// a full-page PNG of every dashboard page with headless Chromium so the UI can be reviewed
/// by looking at it. Activated with `dotnet run --project Auxilia.DevStand -- --screenshots
/// [outputDir]` (default output: artifacts/screenshots under the repo root).
/// </summary>
internal static class ScreenshotHarness
{
    private const string WorkflowType = "pull-request-code-review"; // EndToEndEnvironment.WorkflowType (internal there)

    private static readonly TimeSpan RunCompletionTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan SettleDelay          = TimeSpan.FromMilliseconds(700);

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    public static async Task<int> RunAsync(string? outputDirArg)
    {
        var outputDir = Path.GetFullPath(
            outputDirArg ?? Path.Combine(RepoRoot, "artifacts", "screenshots"));
        Directory.CreateDirectory(outputDir);

        // Provision Chromium BEFORE booting containers — a download failure must not cost a
        // full stack startup. When the browser is already installed this is a cheap no-op.
        Console.WriteLine("Ensuring the Playwright Chromium browser is installed ...");
        var installExitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (installExitCode != 0)
        {
            await Console.Error.WriteLineAsync(
                $"ERROR: 'playwright install chromium' failed with exit code {installExitCode}.");
            return installExitCode;
        }

        Console.WriteLine("Booting the full Auxilia platform stack ...");
        Console.WriteLine("(first run builds Docker images — several minutes; a .prebuilt-images marker in the repo root skips that)");

        var environment = new EndToEndEnvironment();
        await environment.OneTimeSetUp();
        try
        {
            var dashboardUrl =
                $"http://localhost:{EndToEndEnvironment.Backend.GetMappedPublicPort(8080)}";

            Console.WriteLine($"Stack is up ({dashboardUrl}) — triggering a demo Code Review run ...");
            await DemoMail.SendAsync("Please review PR-1 (screenshot harness)", CancellationToken.None);

            var run = await WaitForTerminalRunAsync();
            Console.WriteLine($"Run {run.Id} reached terminal state '{run.State}'.");

            var captured = await CaptureAllPagesAsync(dashboardUrl, run.Id, outputDir);

            Console.WriteLine();
            Console.WriteLine($"=== {captured.Count} screenshots captured ===");
            foreach (var path in captured)
                Console.WriteLine($"  {path}  ({new FileInfo(path).Length / 1024} KB)");
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"ERROR: screenshot capture failed: {exception}");
            return 1;
        }
        finally
        {
            Console.WriteLine("Tearing down containers ...");
            await environment.OneTimeTearDown();
        }
    }

    /// <summary>
    /// Polls the shared Mongo (more robust than scraping the runs page) until the
    /// mail-triggered run reaches a terminal state, so run views have real content.
    /// </summary>
    private static async Task<WorkflowInstanceRecord> WaitForTerminalRunAsync()
    {
        await using var provider = EndToEndEnvironment.BuildPlatformDataProvider();
        var instances = provider.GetRequiredService<IDataAccess<WorkflowInstanceRecord>>();

        var deadline = DateTime.UtcNow + RunCompletionTimeout;
        WorkflowInstanceRecord? latest = null;
        while (DateTime.UtcNow < deadline)
        {
            var all = await instances.ReadAsync(CancellationToken.None);
            latest = all.Where(r => r.WorkflowType == WorkflowType)
                        .OrderByDescending(r => r.CreatedUtc)
                        .FirstOrDefault();
            if (latest?.State is "Success" or "Failed" or "Cancelled")
                return latest;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException(
            $"No '{WorkflowType}' run reached a terminal state within {RunCompletionTimeout} " +
            $"(last observed: {(latest is null ? "none" : $"{latest.Id} in state '{latest.State}'")}).");
    }

    private static async Task<IReadOnlyList<string>> CaptureAllPagesAsync(
        string dashboardUrl, Guid runId, string outputDir)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1600, Height = 900 }
        });
        var page = await context.NewPageAsync();
        var captured = new List<string>();

        // The login page is the only anonymous capture; everything after needs the session.
        await CapturePageAsync(page, dashboardUrl, "/login", "01-login.png", outputDir, captured);
        await LoginAsync(page);

        (string Route, string FileName)[] pages =
        [
            ("/",                           "02-dashboard.png"),
            ("/runs",                       "03-runs.png"),
            ($"/runs/{runId}",              "04-run-detail.png"),
            ("/trigger",                    "05-trigger.png"),
            ("/operator/slots",             "06-operator-slots.png"),
            ("/operator/schedules",         "07-operator-schedules.png"),
            ("/operator/artifact-triggers", "08-operator-artifact-triggers.png"),
            ("/admin",                      "09-admin.png"),
            ("/admin/bundles",              "10-admin-bundles.png"),
            ("/admin/provider-catalog",     "11-admin-provider-catalog.png"),
            ("/audit",                      "12-audit.png")
        ];
        foreach (var (route, fileName) in pages)
            await CapturePageAsync(page, dashboardUrl, route, fileName, outputDir, captured);

        return captured;
    }

    /// <summary>Submits the real login form (sets the session cookie via redirect).</summary>
    private static async Task LoginAsync(IPage page)
    {
        // The page is already on /login from the anonymous capture.
        await page.FillAsync("input[name='username']", "admin");
        await page.FillAsync("input[name='password']", "e2e-admin-pw");
        await page.ClickAsync("button[type='submit']");
        // Success redirects to "/", failure back to "/login?error=1".
        await page.WaitForURLAsync(url => !url.Contains("/login") || url.Contains("error="));
        if (page.Url.Contains("error="))
            throw new InvalidOperationException("Dashboard login was rejected (admin / e2e-admin-pw).");
    }

    private static async Task CapturePageAsync(
        IPage page, string dashboardUrl, string route, string fileName,
        string outputDir, List<string> captured)
    {
        await page.GotoAsync(dashboardUrl + route);

        // Blazor Server renders interactively over the SignalR circuit: network idle alone
        // can fire before the server-side render lands, so add a short settle window.
        try
        {
            await page.WaitForLoadStateAsync(
                LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 10_000 });
        }
        catch (PlaywrightException)
        {
            // The circuit websocket can keep the network "busy" — the settle delay still applies.
        }
        await Task.Delay(SettleDelay);

        var path = Path.Combine(outputDir, fileName);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
        captured.Add(path);
        Console.WriteLine($"  captured {route} -> {path}");
    }
}

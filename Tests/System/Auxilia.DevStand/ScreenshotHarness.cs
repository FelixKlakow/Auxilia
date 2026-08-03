using Auxilia.PlatformData.Entities;
using Auxilia.SystemTestSuite.EndToEnd;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace Auxilia.DevStand;

/// <summary>
/// Visual verification harness: boots the same containerized stack as the interactive dev stand
/// (now including the <c>Auxilia.AdminConsole</c> container), triggers one Code Review run via
/// demo mail, captures the console dashboard while that run is RUNNING (so the live section has
/// content), waits for it to finish, and captures a full-page PNG of every console page with
/// headless Chromium. Activated with `dotnet run --project Auxilia.DevStand -- --screenshots
/// [outputDir]` (default output: artifacts/screenshots under the repo root).
///
/// Auth note: the console container runs with a static Administrator app key (see
/// <see cref="EndToEndEnvironment"/>), so every page renders fully authenticated as that service
/// principal — there is no browser sign-in form to walk (production uses the same-origin
/// Core session cookie instead).
/// </summary>
internal static class ScreenshotHarness
{
    private const string WorkflowType = "pull-request-code-review"; // EndToEndEnvironment.WorkflowType (internal there)

    private static readonly TimeSpan RunCompletionTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan SettleDelay          = TimeSpan.FromMilliseconds(700);

    /// <summary>Repo root, found by walking up to Auxilia.slnx (immune to project depth).</summary>
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Auxilia.slnx")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   $"Auxilia.slnx not found above {AppContext.BaseDirectory}.");
    }

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
            var consoleUrl = EndToEndEnvironment.AdminConsoleUrl;

            Console.WriteLine($"Stack is up ({consoleUrl}) — triggering a demo Code Review run ...");
            await DemoMail.SendAsync("Please review PR-1 (screenshot harness)", CancellationToken.None);

            var captured = await CaptureAllPagesAsync(consoleUrl, outputDir);

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

    private static async Task<IReadOnlyList<string>> CaptureAllPagesAsync(string consoleUrl, string outputDir)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1600, Height = 900 }
        });
        var page = await context.NewPageAsync();
        var captured = new List<string>();

        // The dashboard is captured while the mail-triggered run is active so its
        // "Active now" tile and live list have content.
        var activeRun = await WaitForRunningRunAsync();
        Console.WriteLine($"Run {activeRun.Id} is in state '{activeRun.State}' — capturing the dashboard live ...");
        await CapturePageAsync(page, consoleUrl, "/dashboard", "01-dashboard.png", outputDir, captured);

        var run = await WaitForTerminalRunAsync();
        Console.WriteLine($"Run {run.Id} reached terminal state '{run.State}'.");

        (string Route, string FileName)[] pages =
        [
            ("/",                        "02-home.png"),
            ("/runs",                    "03-runs.png"),
            ($"/runs/{run.Id}",          "04-run-detail.png"),
            ("/workflows",               "05-workflows.png"),
            ("/workflows/new",           "06-workflow-editor.png"),
            ("/connectors",              "07-connectors.png"),
            ("/admin",                   "08-admin-principals.png"),
            ("/admin/provider-catalog",  "09-admin-provider-catalog.png"),
            ("/admin/identity-sources",  "10-admin-identity-sources.png"),
            ("/admin/workflow-types",    "11-admin-workflow-types.png"),
            ("/audit",                   "12-audit.png")
        ];
        foreach (var (route, fileName) in pages)
            await CapturePageAsync(page, consoleUrl, route, fileName, outputDir, captured);

        return captured;
    }

    /// <summary>
    /// Polls the shared Mongo (more robust than scraping the runs page) until the
    /// mail-triggered run reaches the wanted lifecycle stage.
    /// </summary>
    private static async Task<WorkflowInstanceRecord> WaitForRunAsync(
        Func<string, bool> stateReached, string wanted)
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
            if (latest is not null && stateReached(latest.State))
                return latest;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException(
            $"No '{WorkflowType}' run reached {wanted} within {RunCompletionTimeout} " +
            $"(last observed: {(latest is null ? "none" : $"{latest.Id} in state '{latest.State}'")}).");
    }

    private static Task<WorkflowInstanceRecord> WaitForTerminalRunAsync()
        => WaitForRunAsync(state => state is "Success" or "Failed" or "Cancelled", "a terminal state");

    /// <summary>Running, or already terminal when the run was faster than the browser warm-up.</summary>
    private static Task<WorkflowInstanceRecord> WaitForRunningRunAsync()
        => WaitForRunAsync(state => state is "Running" or "Success" or "Failed" or "Cancelled", "'Running'");

    private static async Task CapturePageAsync(
        IPage page, string consoleUrl, string route, string fileName,
        string outputDir, List<string> captured)
    {
        await page.GotoAsync(consoleUrl + route);

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

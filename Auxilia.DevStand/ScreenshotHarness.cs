using System.Text.Json;
using Auxilia.PlatformData.Entities;
using Auxilia.SystemTestSuite.EndToEnd;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace Auxilia.DevStand;

/// <summary>
/// Visual verification harness: boots the same containerized stack as the interactive dev
/// stand, triggers one Code Review run via demo mail, captures the dashboard home while that
/// run is RUNNING (so the Live now section has content), waits for it to finish, and captures
/// a full-page PNG of every dashboard page with headless Chromium so the UI can be reviewed
/// by looking at it. Activated with `dotnet run --project Auxilia.DevStand -- --screenshots
/// [outputDir]` (default output: artifacts/screenshots under the repo root).
/// </summary>
internal static class ScreenshotHarness
{
    private const string WorkflowType = "pull-request-code-review"; // EndToEndEnvironment.WorkflowType (internal there)
    private const string WorkflowPackageUri = "docker://auxilia-code-review-workflow:system-test"; // EndToEndEnvironment.WorkflowPackageUri
    private const string CommandQueue = "workflow.run-commands-e2e"; // EndToEndEnvironment.CommandQueue
    private const string DemoConfigurationName = "team-code-review";

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

            var captured = await CaptureAllPagesAsync(dashboardUrl, outputDir);

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
    /// mail-triggered run reaches the wanted lifecycle stage.
    /// </summary>
    private static async Task<WorkflowInstanceRecord> WaitForRunAsync(
        string workflowType, Func<string, bool> stateReached, string wanted)
    {
        await using var provider = EndToEndEnvironment.BuildPlatformDataProvider();
        var instances = provider.GetRequiredService<IDataAccess<WorkflowInstanceRecord>>();

        var deadline = DateTime.UtcNow + RunCompletionTimeout;
        WorkflowInstanceRecord? latest = null;
        while (DateTime.UtcNow < deadline)
        {
            var all = await instances.ReadAsync(CancellationToken.None);
            latest = all.Where(r => r.WorkflowType == workflowType)
                        .OrderByDescending(r => r.CreatedUtc)
                        .FirstOrDefault();
            if (latest is not null && stateReached(latest.State))
                return latest;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException(
            $"No '{workflowType}' run reached {wanted} within {RunCompletionTimeout} " +
            $"(last observed: {(latest is null ? "none" : $"{latest.Id} in state '{latest.State}'")}).");
    }

    private static Task<WorkflowInstanceRecord> WaitForTerminalRunAsync()
        => WaitForRunAsync(WorkflowType, state => state is "Success" or "Failed" or "Cancelled", "a terminal state");

    /// <summary>Running, or already terminal when the run was faster than the browser warm-up.</summary>
    private static Task<WorkflowInstanceRecord> WaitForRunningRunAsync()
        => WaitForRunAsync(WorkflowType, state => state is "Running" or "Success" or "Failed" or "Cancelled", "'Running'");

    /// <summary>
    /// The flagship demo (#24): dispatches a Claude Code run (in-image stub CLI) and waits
    /// for it to finish so the persisted agent-chat transcript renders on the detail page.
    /// </summary>
    private static async Task<WorkflowInstanceRecord> TriggerAndAwaitClaudeRunAsync()
    {
        await EndToEndEnvironment.MessageBusClient.PublishAsync(
            CommandQueue,
            new RunWorkflowCommand(
                Guid.NewGuid(),
                EndToEndEnvironment.ClaudeWorkflowType,
                EndToEndEnvironment.ClaudeWorkflowPackageUri,
                new Dictionary<string, string>
                {
                    ["Title"] = "Leave a note in the workspace",
                    ["Body"] = "Look around the workspace and leave a short note about what you find."
                },
                RequestedBy: EndToEndEnvironment.RunAsPrincipalId));
        return await WaitForRunAsync(
            EndToEndEnvironment.ClaudeWorkflowType,
            state => state is "Success" or "Failed" or "Cancelled", "a terminal state");
    }

    private static async Task<IReadOnlyList<string>> CaptureAllPagesAsync(string dashboardUrl, string outputDir)
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

        // Dashboard home is captured while the mail-triggered run is active so the
        // Live now section (#21) shows a pulsing Running row.
        var activeRun = await WaitForRunningRunAsync();
        Console.WriteLine($"Run {activeRun.Id} is in state '{activeRun.State}' — capturing the dashboard home live ...");
        await CapturePageAsync(page, dashboardUrl, "/", "02-dashboard.png", outputDir, captured);

        var run = await WaitForTerminalRunAsync();
        Console.WriteLine($"Run {run.Id} reached terminal state '{run.State}'.");

        Console.WriteLine("Seeding demo data (provider catalog, demo configuration, run history) ...");
        await SeedWorkflowEditorDemoDataAsync();
        await SeedRunHistoryDemoDataAsync(run);
        await SeedIdentitySourceDemoDataAsync();

        var configurationId = WorkflowConfigurationRecord.IdFor(DemoConfigurationName);
        (string Route, string FileName)[] pages =
        [
            ("/workflows",                  "03-workflows.png"),
            ("/runs",                       "05-runs.png"),
            ($"/runs/{run.Id}",             "06-run-detail.png"),
            ("/trigger",                    "07-trigger.png"),
            ("/operator/slots",             "08-operator-slots.png"),
            ("/admin",                      "11-admin.png"),
            ("/admin/provider-catalog",     "13-admin-provider-catalog.png"),
            ("/audit",                      "14-audit.png"),
            ($"/runs?configuration={configurationId}", "15-runs-filtered.png"),
            // The mail-review configuration has a mailbox trigger and the code-review
            // output — the flow view shows the full trigger → workflow → output pipeline.
            ($"/workflows/{WorkflowConfigurationRecord.IdFor(EndToEndEnvironment.MailReviewConfigurationName)}/flow",
                "18-workflow-flow.png")
        ];
        foreach (var (route, fileName) in pages)
            await CapturePageAsync(page, dashboardUrl, route, fileName, outputDir, captured);

        // The editor needs interaction before its screenshot is meaningful: a provider must
        // be chosen so the generated settings form is visible (04 sorts it next to the list).
        await CaptureWorkflowEditorAsync(page, dashboardUrl, outputDir, captured);

        // Identity sources (#23): pick the LDAP connector so the generated settings form
        // (host, bind DN, filters, ...) is on screen alongside the seeded source list.
        await CaptureIdentitySourcesAsync(page, dashboardUrl, outputDir, captured);

        // Claude Code (#24): a finished agent run whose transcript renders in the
        // BlazorAgentView agent-chat view on the run detail page.
        Console.WriteLine("Dispatching a Claude Code run (stub CLI) ...");
        var claudeRun = await TriggerAndAwaitClaudeRunAsync();
        Console.WriteLine($"Claude Code run {claudeRun.Id} reached terminal state '{claudeRun.State}'.");
        await CapturePageAsync(page, dashboardUrl, $"/runs/{claudeRun.Id}", "17-claude-code-run.png", outputDir, captured);

        return captured;
    }

    /// <summary>
    /// Curates the provider catalog (availability is deny-by-default), seeds one named workflow
    /// configuration over the same bus path the editor uses, and wires a daily schedule to it so
    /// the workflows page shows a fully populated card.
    /// </summary>
    private static async Task SeedWorkflowEditorDemoDataAsync()
    {
        await using var provider = EndToEndEnvironment.BuildPlatformDataProvider();

        var catalog = provider.GetRequiredService<IDataAccess<ProviderCatalogRecord>>();
        await catalog.SaveAsync(new ProviderCatalogRecord
        {
            Id = ProviderCatalogRecord.IdFor("email-work-items"),
            ProviderType = "email-work-items",
            Available = true,
            Category = "task-source"
        });
        await catalog.SaveAsync(new ProviderCatalogRecord
        {
            Id = ProviderCatalogRecord.IdFor("fake-code-review-happy"),
            ProviderType = "fake-code-review-happy",
            Available = true,
            Category = "code-review"
        });

        await EndToEndEnvironment.MessageBusClient.PublishAsync(
            CommandQueue + "-slot-seed.upsert-configuration",
            new UpsertWorkflowConfigurationCommand(
                DemoConfigurationName, "Team code review", WorkflowType, WorkflowPackageUri,
                Enabled: true,
                [
                    // The mailbox is bound via the reusable "team-mailbox" slot instance the
                    // environment seeded — configure once, reference everywhere.
                    new SlotBindingSeed("work-items", "", new Dictionary<string, string>(),
                        SlotInstanceRecord.IdFor("team-mailbox")),
                    new SlotBindingSeed("repository", "fake-code-review-happy", new Dictionary<string, string>())
                ]));

        var schedules = provider.GetRequiredService<IDataAccess<ScheduledTriggerRecord>>();
        await schedules.SaveAsync(new ScheduledTriggerRecord
        {
            Id = ScheduledTriggerRecord.IdForConfiguration(DemoConfigurationName),
            WorkflowType = WorkflowType,
            WorkflowPackageUri = WorkflowPackageUri,
            IntervalSeconds = 86400,
            Enabled = true,
            // Pre-stamped so the daily schedule does not fire during the capture session.
            LastDispatchedUtc = DateTimeOffset.UtcNow,
            WorkflowConfigurationId = WorkflowConfigurationRecord.IdFor(DemoConfigurationName)
        });

        await Task.Delay(TimeSpan.FromSeconds(1)); // bus seed propagation window
    }

    /// <summary>
    /// Makes the run-centric surfaces (#21) worth photographing: adopts the finished
    /// mail-triggered run into the demo configuration (so the filtered run-history page has
    /// content) and adds a finished rerun of it plus one schedule-dispatched run, giving the
    /// trigger-origin column and the lineage chips real data.
    /// </summary>
    private static async Task SeedRunHistoryDemoDataAsync(WorkflowInstanceRecord run)
    {
        await using var provider = EndToEndEnvironment.BuildPlatformDataProvider();
        var instances = provider.GetRequiredService<IDataAccess<WorkflowInstanceRecord>>();
        var configurationId = WorkflowConfigurationRecord.IdFor(DemoConfigurationName);

        await instances.SaveAsync(run with
        {
            WorkflowConfigurationId = configurationId,
            WorkflowConfigurationName = DemoConfigurationName
        });

        var original = run.DispatchCommandJson is null
            ? null
            : JsonSerializer.Deserialize<RunWorkflowCommand>(run.DispatchCommandJson);
        var rerunContext = new Dictionary<string, string>(
            original?.Context ?? new Dictionary<string, string>())
        {
            ["RERUN_OF"] = run.Id.ToString("D")
        };
        var rerunCommand = new RunWorkflowCommand(
            Guid.NewGuid(), WorkflowType, WorkflowPackageUri, rerunContext,
            original?.RequestedBy, configurationId);
        await instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = WorkflowType,
            State = "Success",
            CreatedUtc = DateTimeOffset.UtcNow.AddSeconds(-90),
            CompletedUtc = DateTimeOffset.UtcNow.AddSeconds(-25),
            DispatchCommandJson = JsonSerializer.Serialize(rerunCommand),
            WorkflowConfigurationId = configurationId,
            WorkflowConfigurationName = DemoConfigurationName
        });

        var scheduleCommand = new RunWorkflowCommand(
            Guid.NewGuid(), WorkflowType, WorkflowPackageUri,
            new Dictionary<string, string>(), null, configurationId);
        await instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = WorkflowType,
            State = "Success",
            CreatedUtc = DateTimeOffset.UtcNow.AddHours(-3),
            CompletedUtc = DateTimeOffset.UtcNow.AddHours(-3).AddSeconds(70),
            DispatchCommandJson = JsonSerializer.Serialize(scheduleCommand),
            WorkflowConfigurationId = configurationId,
            WorkflowConfigurationName = DemoConfigurationName
        });
    }

    /// <summary>
    /// One configured LDAP source with a finished import, so the identity-sources list shows
    /// a populated row. The EndToEnd backend runs without a protection key
    /// (NullSettingsProtector), so plain settings JSON is the correct stored format here.
    /// </summary>
    private static async Task SeedIdentitySourceDemoDataAsync()
    {
        await using var provider = EndToEndEnvironment.BuildPlatformDataProvider();
        var sources = provider.GetRequiredService<IDataAccess<IdentitySourceRecord>>();
        await sources.SaveAsync(new IdentitySourceRecord
        {
            Id = IdentitySourceRecord.IdFor("corporate-directory"),
            Name = "Corporate directory",
            ConnectorType = "ldap",
            ProtectedSettingsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["Host"] = "ldap.corp.example.org",
                ["Port"] = "389",
                ["UseSsl"] = "false",
                ["BindDn"] = "cn=auxilia-import,ou=services,dc=corp,dc=example,dc=org",
                ["BindPassword"] = "demo-not-a-real-secret",
                ["BaseDn"] = "ou=people,dc=corp,dc=example,dc=org",
                ["UserFilter"] = "(objectClass=person)",
                ["UsernameAttribute"] = "sAMAccountName",
                ["DisplayNameAttribute"] = "displayName",
                ["GroupAttribute"] = "memberOf"
            }),
            DefaultRole = "User",
            GroupRoleMappingsJson = """{"Platform Operators":"Operator"}""",
            DisableMissing = true,
            LastImportSummaryJson = JsonSerializer.Serialize(
                new { Created = 12, Updated = 3, Disabled = 1, Skipped = 27, Warnings = Array.Empty<string>() }),
            LastImportUtc = DateTimeOffset.UtcNow.AddHours(-2)
        });
    }

    /// <summary>
    /// The identity-sources page becomes meaningful once a connector is chosen: clicking the
    /// LDAP card reveals the descriptor-generated settings form with its AD hints.
    /// </summary>
    private static async Task CaptureIdentitySourcesAsync(
        IPage page, string dashboardUrl, string outputDir, List<string> captured)
    {
        await page.GotoAsync(dashboardUrl + "/admin/identity-sources");
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

        await page.ClickAsync(".provider-card:has-text('LDAP / Active Directory')");
        await Task.Delay(SettleDelay);

        var path = Path.Combine(outputDir, "16-admin-identity-sources.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
        captured.Add(path);
        Console.WriteLine($"  captured /admin/identity-sources (LDAP connector chosen) -> {path}");
    }

    /// <summary>
    /// Walks the create flow far enough that the screenshot shows the real editing experience:
    /// basics filled, the work-items slot added, the email provider chosen, and the generated
    /// settings form (from the provider's manifest descriptors) on screen.
    /// </summary>
    private static async Task CaptureWorkflowEditorAsync(
        IPage page, string dashboardUrl, string outputDir, List<string> captured)
    {
        await page.GotoAsync(dashboardUrl + "/workflows/new");
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

        // @bind commits on the change event — Tab after each fill.
        await page.FillAsync("input[placeholder='e.g. Code review for team mailbox']", "Code review via team mailbox");
        await page.Keyboard.PressAsync("Tab");
        // Picking the registered workflow lays out its declared slots (required ones pre-bound).
        await page.SelectOptionAsync("#workflow-select", WorkflowType);
        await Task.Delay(SettleDelay);

        // The work-items slot offers the seeded reusable instance — pick it (instance-first UX).
        await page.ClickAsync(".provider-card:has-text('Team mailbox')");
        await Task.Delay(SettleDelay);

        var path = Path.Combine(outputDir, "04-workflow-editor.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
        captured.Add(path);
        Console.WriteLine($"  captured /workflows/new (provider chosen) -> {path}");
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

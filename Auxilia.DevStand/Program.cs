using System.Diagnostics;
using Auxilia.DevStand;
using Auxilia.SystemTestSuite.EndToEnd;
using Auxilia.Workflows.Messaging.Messages;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

// Interactive dev stand: boots the SAME environment as the EndToEnd acceptance test —
// GreenMail, RabbitMQ, MongoDB, Steering Instance, Backend Service with the email task
// source and seeded Code Review slot configurations — and keeps it running until you quit,
// so the dashboard can be explored in a browser. F5-able from Visual Studio.
//
// Screenshot mode (`-- --screenshots [outputDir]`): no interactive loop — triggers one demo
// run, captures every dashboard page as a full-page PNG, tears down, and exits.

if (args.Length > 0 && args[0] == "--screenshots")
    return await ScreenshotHarness.RunAsync(args.Length > 1 ? args[1] : null);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("Booting the full Auxilia platform stack ...");
Console.WriteLine("(first run builds Docker images — several minutes; a .prebuilt-images marker in the repo root skips that)");
Console.WriteLine();

var environment = new EndToEndEnvironment();
await environment.OneTimeSetUp();
try
{
    var dashboardUrl = $"http://localhost:{EndToEndEnvironment.Backend.GetMappedPublicPort(8080)}";

    // With a real key on the host, Claude Code runs are the real thing: override the
    // stub-CLI seed with the default `claude` binary baked into the workflow image.
    var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    var claudeIsReal = !string.IsNullOrWhiteSpace(anthropicKey);
    if (claudeIsReal)
    {
        await EndToEndEnvironment.MessageBusClient.PublishAsync(
            EndToEndEnvironment.CommandQueue + "-slot-seed.upsert",
            new UpsertSlotConfigurationCommand(
                EndToEndEnvironment.ClaudeWorkflowType, "coding-agent", "claude-code-cli",
                new Dictionary<string, string> { ["ApiKey"] = anthropicKey! }));
    }

    Console.WriteLine();
    Console.WriteLine("=== Auxilia dev stand is up ===");
    Console.WriteLine($"  Dashboard : {dashboardUrl}   (login: admin / e2e-admin-pw)");
    Console.WriteLine($"  GreenMail : IMAP localhost:{EndToEndEnvironment.MappedImap}, " +
                      $"SMTP localhost:{EndToEndEnvironment.MappedSmtp}  (auth disabled — any address logs in)");
    Console.WriteLine($"  MongoDB   : {EndToEndEnvironment.MongoConnectionString}  (database 'Auxilia')");
    Console.WriteLine($"  Claude    : {(claudeIsReal
        ? "REAL Claude Code CLI (ANTHROPIC_API_KEY found)"
        : "stub CLI (set ANTHROPIC_API_KEY before starting for real runs)")}");
    Console.WriteLine();
    Console.WriteLine("  [m] send a demo mail (triggers a Code Review run)   [c] run Claude Code   [o] open dashboard   [q] quit");
    Console.WriteLine();

    OpenBrowser(dashboardUrl);

    if (Console.IsInputRedirected)
    {
        // Non-interactive host (e.g. piped output): stay up until the process is terminated.
        while (!cts.IsCancellationRequested)
            await Task.Delay(500);
    }
    else
    {
        var demoCounter = 0;
        while (!cts.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(150);
                continue;
            }

            switch (Console.ReadKey(intercept: true).Key)
            {
                case ConsoleKey.Q:
                    cts.Cancel();
                    break;
                case ConsoleKey.O:
                    OpenBrowser(dashboardUrl);
                    break;
                case ConsoleKey.M:
                    var subject = $"Please review PR-{++demoCounter} (dev stand)";
                    await DemoMail.SendAsync(subject, cts.Token);
                    Console.WriteLine($"  -> mail sent: \"{subject}\" — the run appears on the dashboard within a few seconds.");
                    _ = WatchForReplyAsync(subject, cts.Token);
                    break;
                case ConsoleKey.C:
                    await EndToEndEnvironment.MessageBusClient.PublishAsync(
                        EndToEndEnvironment.CommandQueue,
                        new RunWorkflowCommand(
                            Guid.NewGuid(),
                            EndToEndEnvironment.ClaudeWorkflowType,
                            EndToEndEnvironment.ClaudeWorkflowPackageUri,
                            new Dictionary<string, string>
                            {
                                ["Title"] = $"Dev-stand Claude Code session #{++demoCounter}",
                                ["Body"] = "Look around the workspace and leave a short note about what you find."
                            },
                            RequestedBy: EndToEndEnvironment.RunAsPrincipalId),
                        cts.Token);
                    Console.WriteLine(
                        "  -> Claude Code run dispatched — open the dashboard's Live now section to watch the agent chat.");
                    break;
            }
        }
    }
}
finally
{
    Console.WriteLine("Tearing down containers ...");
    await environment.OneTimeTearDown();
}

return 0;

static void OpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch
    {
        // No default browser (headless host) — the URL is printed either way.
    }
}

// The Code Review workflow writes its summary back as a mail reply — announce it when it lands.
static async Task WatchForReplyAsync(string subject, CancellationToken ct)
{
    try
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            using var imap = new ImapClient();
            await imap.ConnectAsync(
                EndToEndEnvironment.MailHost, EndToEndEnvironment.MappedImap,
                SecureSocketOptions.None, ct);
            await imap.AuthenticateAsync(DemoMail.OperatorMailbox, "pw", ct);
            var inbox = imap.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

            for (var i = 0; i < inbox.Count; i++)
            {
                var mail = await inbox.GetMessageAsync(i, ct);
                if (mail.Subject == $"Re: {subject}")
                {
                    Console.WriteLine($"  <- review reply received for \"{subject}\":");
                    Console.WriteLine("     " + mail.TextBody?.ReplaceLineEndings("\n     "));
                    return;
                }
            }

            await imap.DisconnectAsync(quit: true, ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }
    catch (OperationCanceledException)
    {
        // Shutting down — nothing to announce.
    }
}

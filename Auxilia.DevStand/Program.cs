using Auxilia.DevStand;
using Auxilia.SystemTestSuite.EndToEnd;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

// Interactive dev stand: boots the SAME environment as the EndToEnd acceptance test —
// GreenMail, RabbitMQ, MongoDB, Core.Runner, Core.Api, and WorkflowStudio (email task source),
// with the mail-review configuration seeded — and keeps it running until you quit, so the mail
// path can be exercised. F5-able from Visual Studio.
//
// NOTE (BackendService retirement, Phase 4): the operator dashboard has moved out of the retired
// BackendService into the new Auxilia.AdminConsole (a pure Core.Api client). The console is NOT yet
// wired into this dev stand, so there is no dashboard URL to open. TODO(Phase 4+): boot the
// AdminConsole container here (same-origin with Core.Api) and restore the browser + screenshot flows.

// Screenshot mode (`-- --screenshots [outputDir]`): parked until the AdminConsole is wired in.
if (args.Length > 0 && args[0] == "--screenshots")
    return await ScreenshotHarness.RunAsync(args.Length > 1 ? args[1] : null);

// Preview of the mail [s] would send (parsed from demo-session-mail.md) — no stack boot.
if (args.Contains("--print-session-mail"))
{
    var (previewSubject, previewBody, previewSource) = DemoMail.LoadSessionMail(1);
    Console.WriteLine($"source : {previewSource ?? "(built-in fallback)"}");
    Console.WriteLine($"subject: {previewSubject}");
    Console.WriteLine(previewBody);
    return 0;
}

EndToEndEnvironment.PresentationMode = args.Contains("--presentation");

// Durable stand: Mongo data lives in a named Docker volume, so configured triggers survive restarts.
if (args.Contains("--keep-data"))
    EndToEndEnvironment.DataVolumeName = "auxilia-devstand";

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("Booting the full Auxilia platform stack ...");
Console.WriteLine("(first run builds Docker images — several minutes; a .prebuilt-images marker in the repo root skips that)");
Console.WriteLine();

var environment = new EndToEndEnvironment();
await environment.OneTimeSetUp();
try
{
    Console.WriteLine();
    Console.WriteLine("=== Auxilia dev stand is up ===");
    Console.WriteLine($"  Core.Api  : http://localhost:{EndToEndEnvironment.CoreApi.GetMappedPublicPort(8080)}   (bearer API key)");
    Console.WriteLine($"  GreenMail : IMAP localhost:{EndToEndEnvironment.MappedImap}, " +
                      $"SMTP localhost:{EndToEndEnvironment.MappedSmtp}  (auth disabled — any address logs in)");
    Console.WriteLine($"  MongoDB   : {EndToEndEnvironment.MongoConnectionString}  (database 'Auxilia')");
    Console.WriteLine("  Dashboard : moved to Auxilia.AdminConsole — not yet wired into this stand (TODO Phase 4+).");
    Console.WriteLine($"  Data      : {(EndToEndEnvironment.DataVolumeName is null
        ? "ephemeral (start with --keep-data to keep seeded triggers across restarts)"
        : $"durable in Docker volumes '{EndToEndEnvironment.DataVolumeName}-*'")}");
    Console.WriteLine();
    Console.WriteLine("  [m] send a demo mail (triggers a Code Review run via Studio)   [s] send a session mail   [q] quit");
    Console.WriteLine();

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
                case ConsoleKey.M:
                    var subject = $"Please review PR-{++demoCounter} (dev stand)";
                    await DemoMail.SendAsync(subject, cts.Token);
                    Console.WriteLine($"  -> mail sent: \"{subject}\" — the run dispatches once the trigger polls.");
                    _ = WatchForReplyAsync(subject, cts.Token);
                    break;
                case ConsoleKey.S:
                    var (sessionSubject, sessionBody, mailSource) = DemoMail.LoadSessionMail(++demoCounter);
                    await DemoMail.SendAsync(sessionSubject, sessionBody, cts.Token);
                    Console.WriteLine($"  -> mail sent: \"{sessionSubject}\" " +
                                      $"({(mailSource is null ? "built-in mail" : mailSource)}).");
                    _ = WatchForReplyAsync(sessionSubject, cts.Token);
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

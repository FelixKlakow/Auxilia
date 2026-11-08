using System.Security.Cryptography;
using System.Text;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Dummy workflow proving a slot credential is re-read while the run goes on: it reports the
/// FINGERPRINT (never the value) of its <c>secret</c> slot's token at start, waits for any run
/// input, fetches the slot again through <see cref="ISlotConfigurationSource"/> — a real
/// activation through runner and Core — and reports the fingerprint of what the connector holds
/// then. A token its owner replaced in between shows up as the new fingerprint.
/// </summary>
public static class CredentialRefreshWorkflow
{
    public const string WorkflowName = "credential-refresh-workflow";
    public const string ViewName = "credential";
    public const string SlotName = "secret";

    /// <summary>One reading of the slot's token: when, and whose (by fingerprint).</summary>
    public sealed record CredentialReading(string Phase, string Fingerprint);

    public static Task RunAsync(string[] args) =>
        WorkflowBuilder
            .Create(WorkflowName)
            .RequiresCredentialProbe(SlotName, new CredentialProbeCapabilities())
            .DeclaresView<CredentialReading>(ViewName, ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            .WithApplication(ExecuteAsync)
            .Run(args);

    /// <summary>A token's fingerprint: enough to tell tokens apart, nothing to use one.</summary>
    public static string Fingerprint(string? token)
        => token is null ? "none" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];

    private static async Task ExecuteAsync(IServiceProvider provider, CancellationToken ct)
    {
        var views = provider.GetRequiredService<IViewPublisher>();
        var probe = provider.GetRequiredKeyedService<ICredentialProbe>(SlotName);
        await ReportAsync(views, "start", probe.GetSetting("token"), ct);

        // The owner may replace the connector's token meanwhile; any input asks for a fresh read.
        await provider.GetRequiredService<IWorkflowInputs>().ReceiveAsync(ct);
        var again = await provider.GetRequiredService<ISlotConfigurationSource>().FetchAsync(SlotName, ct);
        await ReportAsync(views, "re-read", again.Settings.GetValueOrDefault("token"), ct);
    }

    /// <summary>Publishes a reading — and logs it, so the container log exists to be checked for tokens.</summary>
    private static async Task ReportAsync(IViewPublisher views, string phase, string? token, CancellationToken ct)
    {
        var reading = new CredentialReading(phase, Fingerprint(token));
        Console.WriteLine($"[CredentialRefreshWorkflow] {reading.Phase}: {reading.Fingerprint}");
        await views.PublishAsync(ViewName, reading, ct);
    }
}

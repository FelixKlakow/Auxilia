using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.BackendService.Dashboard;

/// <summary>The dashboard's at-a-glance numbers, recent runs, warnings, and setup checklist.</summary>
public sealed record DashboardStats(
    int Runs24h,
    int Failures24h,
    int? SuccessRatePercent,
    int EnabledConfigurations,
    IReadOnlyList<WorkflowInstanceRecord> RecentRuns,
    IReadOnlyList<AttentionItem> Attention,
    IReadOnlyList<GettingStartedStep> GettingStarted)
{
    public bool SetupComplete => GettingStarted.All(s => s.Done);
}

/// <summary>Something an operator should look at, with where to fix it.</summary>
public sealed record AttentionItem(string Text, string Href);

/// <summary>One step of the first-run checklist shown while the platform is still being set up.</summary>
public sealed record GettingStartedStep(string Title, string Description, string Href, bool Done);

public sealed class DashboardStatsService(
    IDataAccess<WorkflowInstanceRecord> instances,
    IDataAccess<WorkflowConfigurationRecord> configurations,
    IDataAccess<SlotInstanceRecord> slotInstances,
    IDataAccess<ScheduledTriggerRecord> scheduledTriggers,
    IDataAccess<MailboxTriggerRecord> mailboxTriggers,
    IDataAccess<ArtifactTriggerRecord> artifactTriggers,
    ProviderCatalogService catalog,
    TimeProvider timeProvider)
{
    private const int RecentRunCount = 8;
    private static readonly string[] FailureStates = ["Failed", "PreFlightFailed"];

    public async Task<DashboardStats> GetAsync(CancellationToken ct = default)
    {
        var windowStart = timeProvider.GetUtcNow().AddHours(-24);
        var runs = (await instances.ReadAsync(ct)).ToList();
        var configs = (await configurations.ReadAsync(ct)).ToList();
        var knownInstanceIds = (await slotInstances.ReadAsync(ct)).ToList().Select(i => i.Id).ToHashSet();

        var terminal24h = runs
            .Where(r => WorkflowRerunService.IsTerminal(r) && (r.CompletedUtc ?? r.CreatedUtc) >= windowStart)
            .ToList();
        var failures24h = terminal24h.Count(r => FailureStates.Contains(r.State));

        return new DashboardStats(
            runs.Count(r => r.CreatedUtc >= windowStart),
            failures24h,
            terminal24h.Count == 0
                ? null
                : (int)Math.Round(100.0 * terminal24h.Count(r => r.State == "Success") / terminal24h.Count),
            configs.Count(c => c.Enabled),
            runs.OrderByDescending(r => r.CreatedUtc).Take(RecentRunCount).ToList(),
            await AttentionOf(configs, knownInstanceIds, failures24h),
            await GettingStartedOf(runs, configs, knownInstanceIds, ct));
    }

    /// <summary>Broken references and recent failures — the things that silently rot until someone looks.</summary>
    private static Task<List<AttentionItem>> AttentionOf(
        List<WorkflowConfigurationRecord> configs, HashSet<Guid> knownInstanceIds, int failures24h)
    {
        var attention = new List<AttentionItem>();
        foreach (var config in configs.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (BindingsOf(config).Any(b => b.SlotInstanceId is { } id && !knownInstanceIds.Contains(id)))
                attention.Add(new AttentionItem(
                    $"\"{config.DisplayName}\" references a deleted slot instance — its runs will fail pre-flight.",
                    $"workflows/{config.Id}/edit"));
        }

        if (failures24h > 0)
            attention.Add(new AttentionItem(
                $"{failures24h} run{(failures24h == 1 ? "" : "s")} failed in the last 24 hours.", "runs"));
        return Task.FromResult(attention);
    }

    private async Task<List<GettingStartedStep>> GettingStartedOf(
        List<WorkflowInstanceRecord> runs, List<WorkflowConfigurationRecord> configs,
        HashSet<Guid> knownInstanceIds, CancellationToken ct)
    {
        var anyProviderAvailable = (await catalog.ListAsync(ct)).Any(p => p.Available);
        var anyTrigger =
            (await scheduledTriggers.ReadAsync(ct)).ToList().Count > 0
            || (await mailboxTriggers.ReadAsync(ct)).ToList().Count > 0
            || (await artifactTriggers.ReadAsync(ct)).ToList().Count > 0;

        return
        [
            new GettingStartedStep("Make a provider available",
                "Providers are deny-by-default — enable the ones this company may use.",
                "admin/provider-catalog", anyProviderAvailable),
            new GettingStartedStep("Create a slot instance",
                "Configure a provider once (the team mailbox, a license seat) and reuse it everywhere.",
                "operator/slots", knownInstanceIds.Count > 0),
            new GettingStartedStep("Configure a workflow",
                "Pick a registered workflow and bind its slots.",
                "workflows/new", configs.Count > 0),
            new GettingStartedStep("Wire a trigger",
                "Add a schedule, mailbox, or artifact chain so runs start on their own.",
                "workflows", anyTrigger),
            new GettingStartedStep("Watch the first run",
                "Triggered runs appear under Runs with live views.",
                "runs", runs.Count > 0)
        ];
    }

    private static List<WorkflowConfigurationSlotBinding> BindingsOf(WorkflowConfigurationRecord config)
    {
        try
        {
            return JsonSerializer.Deserialize<List<WorkflowConfigurationSlotBinding>>(config.SlotBindingsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

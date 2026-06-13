using System.Text.Json;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// Run-centric surfaces (#21): the dashboard's Live now section, the per-configuration run
/// history on the Runs page, and the rerun lineage chips on the run detail page — all
/// rendered through the real host with seeded platform data and the fake bus.
/// </summary>
[TestFixture]
[Category("Component")]
public class RunSurfacesTests : DashboardComponentTestBase
{
    private IDataAccess<WorkflowInstanceRecord> Instances
        => Factory.Services.GetRequiredService<IDataAccess<WorkflowInstanceRecord>>();

    private static RunWorkflowCommand Command(
        Dictionary<string, string> context, Guid? configurationId = null)
        => new(Guid.NewGuid(), "code-review", "docker://review:1", context,
            RequestedBy: Guid.NewGuid(), WorkflowConfigurationId: configurationId);

    [Test]
    public async Task DashboardHome_ShowsTheActiveRun_AndHidesItAfterTheTerminalTransition()
    {
        var instanceId = Guid.NewGuid();
        await Instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            WorkflowType = "code-review",
            State = "Running",
            CreatedUtc = DateTimeOffset.UtcNow,
            WorkflowConfigurationName = "team-code-review"
        });
        using var client = CreateClient();
        var (cookie, _) = await LoginAsync(client, AdminUsername, AdminPassword);

        var html = await GetHtmlAsync(client, "/dashboard", cookie);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Live now"));
            Assert.That(html, Does.Contain("team-code-review"), "the configuration name is the row label");
            Assert.That(html, Does.Contain("badge-running"), "the Running state badge pulses via this class");
            Assert.That(html, Does.Contain($"runs/{instanceId}"), "the row links to the run detail page");
        });

        // Terminal transition as the Steering Instance performs it: persisted state change
        // plus the lifecycle event on the bus (which live circuits consume via the broker).
        await Instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            WorkflowType = "code-review",
            State = "Success",
            CreatedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = DateTimeOffset.UtcNow,
            WorkflowConfigurationName = "team-code-review"
        });
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(instanceId, "code-review", "Success", null, DateTimeOffset.UtcNow));

        html = await GetHtmlAsync(client, "/dashboard", cookie);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Nothing running right now"));
            Assert.That(html, Does.Not.Contain("badge-running"));
        });
    }

    [Test]
    public async Task FilteredRuns_ShowsTheConfigurationHeaderCard_AndTheTriggerOriginColumn()
    {
        var configurations = Factory.Services.GetRequiredService<IDataAccess<WorkflowConfigurationRecord>>();
        var configurationId = WorkflowConfigurationRecord.IdFor("history-config");
        await configurations.SaveAsync(new WorkflowConfigurationRecord
        {
            Id = configurationId,
            Name = "history-config",
            DisplayName = "History code review",
            WorkflowType = "code-review",
            PackageUri = "docker://review:1",
            Enabled = true,
            SlotBindingsJson = "[]"
        });

        var predecessorId = Guid.NewGuid();
        await SeedConfiguredRunAsync(configurationId, Command(new Dictionary<string, string>
        {
            ["WorkItemId"] = "mail-0123",
            ["Title"] = "Review PR-7",
            ["From"] = "dev@example.com"
        }, configurationId));
        await SeedConfiguredRunAsync(configurationId, Command(new Dictionary<string, string>
        {
            ["RERUN_OF"] = predecessorId.ToString("D")
        }, configurationId));
        await SeedConfiguredRunAsync(configurationId, Command(new Dictionary<string, string>(), configurationId));

        using var client = CreateClient();
        var (cookie, _) = await LoginAsync(client, AdminUsername, AdminPassword);

        var html = await GetHtmlAsync(client, $"/runs?configuration={configurationId}", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("History code review"), "header card shows the display name");
            Assert.That(html, Does.Contain("Enabled"), "header card shows the enabled state");
            Assert.That(html, Does.Contain($"workflows/{configurationId}/edit"), "header card links to the editor");
            Assert.That(html, Does.Contain("Trigger origin"), "the history table gains the origin column");
            // The "·" separator is entity-encoded by Razor — assert on the stable parts.
            Assert.That(html, Does.Contain("Review PR-7"), "the mail origin surfaces the subject");
            Assert.That(html, Does.Contain($"Rerun of {predecessorId.ToString("N")[..8]}"));
            Assert.That(html, Does.Contain($"runs/{predecessorId}"), "the rerun origin links to the predecessor");
            Assert.That(html, Does.Contain("title=\"Schedule\""),
                "the configuration-bound run without adapter context reads as a schedule dispatch");
        });
    }

    [Test]
    public async Task RunDetail_ShowsPredecessorAndSuccessorChips()
    {
        var predecessorId = Guid.NewGuid();
        var successorId = Guid.NewGuid();
        await Instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = predecessorId,
            WorkflowType = "code-review",
            State = "Success",
            CreatedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            CompletedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            DispatchCommandJson = JsonSerializer.Serialize(Command(new Dictionary<string, string>()))
        });
        await Instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = successorId,
            WorkflowType = "code-review",
            State = "Success",
            CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedUtc = DateTimeOffset.UtcNow.AddMinutes(-4),
            DispatchCommandJson = JsonSerializer.Serialize(Command(new Dictionary<string, string>
            {
                ["RERUN_OF"] = predecessorId.ToString("D")
            }))
        });
        using var client = CreateClient();
        var (cookie, _) = await LoginAsync(client, AdminUsername, AdminPassword);

        var successorHtml = await GetHtmlAsync(client, $"/runs/{successorId}", cookie);
        Assert.Multiple(() =>
        {
            Assert.That(successorHtml, Does.Contain($"Rerun of {predecessorId.ToString("N")[..8]}"));
            Assert.That(successorHtml, Does.Contain($"runs/{predecessorId}"), "the chip links back to the predecessor");
        });

        var predecessorHtml = await GetHtmlAsync(client, $"/runs/{predecessorId}", cookie);
        Assert.Multiple(() =>
        {
            Assert.That(predecessorHtml, Does.Contain($"Rerun #{successorId.ToString("N")[..8]} started"));
            Assert.That(predecessorHtml, Does.Contain($"runs/{successorId}"), "the chip links to the successor");
        });
    }

    private async Task SeedConfiguredRunAsync(Guid configurationId, RunWorkflowCommand command)
        => await Instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "code-review",
            State = "Success",
            CreatedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = DateTimeOffset.UtcNow,
            DispatchCommandJson = JsonSerializer.Serialize(command),
            WorkflowConfigurationId = configurationId,
            WorkflowConfigurationName = "history-config"
        });
}

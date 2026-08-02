using Auxilia.Workflows;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.SimulatedWorkItems;

/// <summary>
/// Slot handler for provider type "simulated-work-items": a scripted TFS stand-in for
/// live-view testing. Every id resolves to the configured story; every operation sleeps the
/// configured delay so runs are watchable in the steering client. Never a production source.
/// </summary>
public sealed class SimulatedWorkItemsSlotHandler : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
    {
        var title = configuration.Settings.GetValueOrDefault("Title") is { Length: > 0 } t
            ? t : "Simulated story";
        var description = configuration.Settings.GetValueOrDefault("Description") is { Length: > 0 } d
            ? d
            : "Append an 'implemented' marker to the repository. (Simulated — the driven stub "
              + "author reacts to the drive prompts, not to this text.)";
        var delay = TimeSpan.FromSeconds(
            double.TryParse(configuration.Settings.GetValueOrDefault("DelaySeconds"),
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
                ? seconds : 3);
        services.AddScoped<IWorkItemAccess>(_ => new SimulatedWorkItemAccess(title, description, delay));
    }
}

internal sealed class SimulatedWorkItemAccess(string title, string description, TimeSpan delay)
    : IWorkItemAccess
{
    private static readonly IReadOnlyList<string> States = ["New", "Active", "Resolved", "Closed"];

    public async Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default)
    {
        await Task.Delay(delay, cancellationToken);
        return new WorkItem(id, title, description, "Active", "Simulation", ["simulated"]);
    }

    public async Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(
        IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var items = new List<WorkItem>();
        foreach (var id in ids)
            if (await GetWorkItemAsync(id, cancellationToken) is { } item)
                items.Add(item);
        return items;
    }

    public Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default)
        => Task.Delay(delay, cancellationToken);

    public async Task<IReadOnlyList<string>> GetStatesAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await Task.Delay(delay, cancellationToken);
        return States;
    }

    public Task SetStateAsync(string id, string state, CancellationToken cancellationToken = default)
        => Task.Delay(delay, cancellationToken);
}

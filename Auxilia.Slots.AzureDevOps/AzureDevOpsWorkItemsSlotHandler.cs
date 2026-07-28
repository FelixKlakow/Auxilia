using Auxilia.Workflows;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.AzureDevOps;

/// <summary>
/// Slot handler for provider type "tfs-account": backs the "work-items" slot with Azure DevOps /
/// TFS work items over the Work Item Tracking REST API — the same connector that authenticates
/// repositories also binds work items.
/// </summary>
public sealed class AzureDevOpsWorkItemsSlotHandler : ISlotHandler
{
    /// <summary>Seam for tests: the HTTP handler the access client is built over.</summary>
    internal HttpMessageHandler? MessageHandler { get; set; }

    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
    {
        switch (slotName)
        {
            case "work-items":
                var orgUrl = RequiredSetting(configuration.Settings, "OrgUrl");
                var token = RequiredSetting(configuration.Settings, "token");
                var handler = MessageHandler;
                services.AddScoped<IWorkItemAccess>(_ => new AzureDevOpsWorkItemAccess(orgUrl, token, handler));
                break;

            default:
                throw new InvalidOperationException($"Unknown slot: {slotName}");
        }
    }

    private static string RequiredSetting(IReadOnlyDictionary<string, string> settings, string key)
        => settings.GetValueOrDefault(key) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"The tfs-account provider requires the '{key}' setting on its connector.");
}

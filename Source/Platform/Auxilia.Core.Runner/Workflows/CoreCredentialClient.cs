using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>Resolves a run's slot credential from Core.Api — the Core encrypts, the runner relays.</summary>
public interface ICoreCredentialClient
{
    Task<ResolvedSlotCredential?> ResolveAsync(
        Guid coreRunId, string resolutionToken, string slotName, string publicKey, CancellationToken ct);

    /// <summary>Non-secret setting values of every binding of a provider in the run, or null when unavailable.</summary>
    Task<ResolvedSlotSettings?> ResolveSettingValuesAsync(
        Guid coreRunId, string resolutionToken, string providerType, IReadOnlyList<string> keys, CancellationToken ct);
}

/// <summary>
/// HTTP client for the Core's token-authenticated slot-resolution endpoint. It presents the
/// run-scoped resolution token (never a principal credential), so the runner can resolve only
/// slots of runs the Core dispatched to it. Returns null on any non-success response — the caller
/// then fails the activation rather than falling back to a local secret store.
/// </summary>
public sealed class CoreCredentialClient(
    IHttpClientFactory httpClientFactory,
    IOptions<WorkflowDispatcherSettings> settings,
    ILogger<CoreCredentialClient> logger) : ICoreCredentialClient
{
    public async Task<ResolvedSlotCredential?> ResolveAsync(
        Guid coreRunId, string resolutionToken, string slotName, string publicKey, CancellationToken ct)
    {
        var baseAddress = settings.Value.CoreApiBaseAddress;
        if (string.IsNullOrWhiteSpace(baseAddress))
            return null;

        var http = httpClientFactory.CreateClient("core-api");
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{baseAddress.TrimEnd('/')}/internal/runs/{coreRunId}/resolve-slot")
        {
            Content = JsonContent.Create(new ResolveSlotRequest(slotName, publicKey))
        };
        request.Headers.Add("X-Resolution-Token", resolutionToken);

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Core slot resolution failed. RunId={RunId} Slot={Slot} Status={Status}",
                    coreRunId, slotName, (int)response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<ResolvedSlotCredential>(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Core slot resolution errored. RunId={RunId} Slot={Slot}", coreRunId, slotName);
            return null;
        }
    }

    public async Task<ResolvedSlotSettings?> ResolveSettingValuesAsync(
        Guid coreRunId, string resolutionToken, string providerType, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var baseAddress = settings.Value.CoreApiBaseAddress;
        if (string.IsNullOrWhiteSpace(baseAddress))
            return null;

        var http = httpClientFactory.CreateClient("core-api");
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{baseAddress.TrimEnd('/')}/internal/runs/{coreRunId}/slot-settings")
        {
            Content = JsonContent.Create(new ResolveSlotSettingsRequest(providerType, keys))
        };
        request.Headers.Add("X-Resolution-Token", resolutionToken);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Core slot-settings resolution failed. RunId={RunId} Provider={Provider} Status={Status}",
                    coreRunId, providerType, (int)response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<ResolvedSlotSettings>(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Core slot-settings resolution errored. RunId={RunId} Provider={Provider}",
                coreRunId, providerType);
            return null;
        }
    }
}

using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>Fetches a run's input files from the Core.</summary>
public interface IRunInputFileSource
{
    /// <summary>
    /// Writes the input file <paramref name="name"/> of the run to <paramref name="destinationPath"/>;
    /// false when the Core does not serve it (no such file, or the run has ended).
    /// </summary>
    Task<bool> DownloadAsync(
        Guid coreRunId, string resolutionToken, string name, string destinationPath, CancellationToken ct);
}

/// <summary>
/// HTTP client for the Core's token-authenticated input-file endpoint: it presents the run-scoped
/// resolution token (never a principal credential), so the runner fetches only files of runs the
/// Core dispatched to it, and only while those runs are live.
/// </summary>
public sealed class CoreRunInputFileClient(
    IHttpClientFactory httpClientFactory,
    IOptions<WorkflowDispatcherSettings> settings,
    ILogger<CoreRunInputFileClient> logger) : IRunInputFileSource
{
    public async Task<bool> DownloadAsync(
        Guid coreRunId, string resolutionToken, string name, string destinationPath, CancellationToken ct)
    {
        var baseAddress = settings.Value.CoreApiBaseAddress;
        if (string.IsNullOrWhiteSpace(baseAddress))
            return false;

        var http = httpClientFactory.CreateClient("core-api");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{baseAddress.TrimEnd('/')}/internal/runs/{coreRunId}/input-files/{Uri.EscapeDataString(name)}");
        request.Headers.Add("X-Resolution-Token", resolutionToken);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Core input-file delivery failed. RunId={RunId} File={File} Status={Status}",
                    coreRunId, name, (int)response.StatusCode);
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var target = File.Create(destinationPath);
            await response.Content.CopyToAsync(target, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Core input-file delivery errored. RunId={RunId} File={File}", coreRunId, name);
            return false;
        }
    }
}

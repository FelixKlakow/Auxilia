using System.Net.Http.Headers;
using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Protection;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>Hands a run's checkpoint to the Core, which encrypts and keeps it.</summary>
public interface IRunCheckpointUploader
{
    /// <summary>Uploads <paramref name="content"/> as the run's checkpoint <paramref name="name"/>; returns the Core's artifact JSON.</summary>
    Task<string> UploadAsync(
        Guid coreRunId, Guid instanceId, string resolutionToken, string name, byte[] content, bool archive,
        CancellationToken ct);
}

/// <summary>
/// Serves the SDK's checkpoint saves (<see cref="RunCheckpointsResource"/>) through the audited
/// resource proxy: the workflow only NAMES a checkpoint — the path comes from its declaration —
/// and the runner reads it from the run's output directory without following links
/// (<see cref="SafeRunOutput"/>; a folder becomes a .tar.gz), then uploads it with the run's
/// resolution token. The Core encrypts it, keeps the latest copy per run, and applies its lifetime.
/// </summary>
public sealed class RunCheckpointConnector(
    WorkflowInstanceRegistry instances,
    WorkflowSchemaStore schemas,
    IRunCheckpointUploader uploader,
    ISettingsProtector settingsProtector,
    IOptions<WorkflowDispatcherSettings> settings,
    AuditLog auditLog,
    ILogger<RunCheckpointConnector> logger) : IResourceConnector
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ResourceName => RunCheckpointsResource.Name;

    public async Task<string> ExecuteAsync(Guid instanceId, string operation, string payloadJson, CancellationToken ct)
    {
        if (operation != RunCheckpointsResource.SaveOperation)
            throw new InvalidOperationException($"unknown checkpoint operation '{operation}'");
        var name = JsonSerializer.Deserialize<RunCheckpointsResource.SaveRequest>(payloadJson, Json)?.Name
                   ?? throw new InvalidOperationException("the save names no checkpoint");

        var instance = await instances.GetAsync(instanceId, ct)
                       ?? throw new InvalidOperationException("the run is not registered with this runner");
        var declared = (await schemas.GetSchemaAsync(instance.WorkflowType, ct))?.Checkpoints
            .FirstOrDefault(c => c.Name == name)
            ?? throw new InvalidOperationException($"'{instance.WorkflowType}' declares no checkpoint '{name}'");
        var command = instance.DispatchCommandJson is { Length: > 0 } stored
                      && JsonSerializer.Deserialize<RunWorkflowCommand>(stored) is { } protectedCommand
            ? DispatchCommandProtection.Unprotect(protectedCommand, settingsProtector, logger)
            : null;
        if (command?.ResolutionToken is not { Length: > 0 } token)
            throw new InvalidOperationException("the run has no resolution context to hand a checkpoint to the Core");

        var (content, archive) = await ReadAsync(OutputDirectoryFor(instanceId), declared, ct);
        var result = await uploader.UploadAsync(command.CommandId, instanceId, token, name, content, archive, ct);
        // Stored — recorded even if the call's deadline runs out right now.
        await auditLog.AppendAsync("steering-instance", "artifact.checkpoint-saved",
            instanceId.ToString(), $"{name} ({content.Length} bytes)", ct: CancellationToken.None);
        return result;
    }

    private string OutputDirectoryFor(Guid instanceId)
        => Path.Combine(settings.Value.RunOutputDirectory, instanceId.ToString("N"));

    private async Task<(byte[] Content, bool Archive)> ReadAsync(
        string outputDirectory, WorkflowCheckpointDescriptor declared, CancellationToken ct)
    {
        var limit = settings.Value.MaxCheckpointBytes;
        switch (SafeRunOutput.KindOf(outputDirectory, declared.RelativePath))
        {
            case SafeRunOutput.EntryKind.File:
                await using (var file = SafeRunOutput.OpenFile(outputDirectory, declared.RelativePath)
                                        ?? throw new InvalidOperationException(
                                            $"checkpoint '{declared.Name}' is not a regular file inside the run's output"))
                {
                    if (file.Length > limit)
                        throw new SafeRunOutput.TooLargeException(limit);
                    using var copy = new MemoryStream();
                    await file.CopyToAsync(copy, ct);
                    return (copy.ToArray(), false);
                }
            case SafeRunOutput.EntryKind.Directory:
                using (var archive = new MemoryStream())
                {
                    await SafeRunOutput.ArchiveDirectoryAsync(outputDirectory, declared.RelativePath, archive, limit, ct);
                    return (archive.ToArray(), true);
                }
            case SafeRunOutput.EntryKind.Missing:
                throw new InvalidOperationException($"checkpoint '{declared.Name}' does not exist yet ({declared.RelativePath})");
            default:
                throw new InvalidOperationException(
                    $"checkpoint '{declared.Name}' is reached through a link — refused");
        }
    }
}

/// <summary>Uploads checkpoints to the Core's token-authorized endpoint (the run must still be live).</summary>
public sealed class CoreRunCheckpointClient(
    IHttpClientFactory httpClientFactory, IOptions<WorkflowDispatcherSettings> settings) : IRunCheckpointUploader
{
    public async Task<string> UploadAsync(
        Guid coreRunId, Guid instanceId, string resolutionToken, string name, byte[] content, bool archive,
        CancellationToken ct)
    {
        var baseAddress = settings.Value.CoreApiBaseAddress;
        if (string.IsNullOrWhiteSpace(baseAddress))
            throw new InvalidOperationException("the runner knows no Core to hand checkpoints to");
        var http = httpClientFactory.CreateClient("core-api");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{baseAddress.TrimEnd('/')}/internal/runs/{coreRunId}/checkpoints/{Uri.EscapeDataString(name)}")
        {
            Content = new ByteArrayContent(content)
            {
                Headers = { ContentType = new MediaTypeHeaderValue(archive ? "application/gzip" : "application/octet-stream") },
            },
        };
        request.Headers.Add("X-Resolution-Token", resolutionToken);
        request.Headers.Add("X-Workflow-Instance", instanceId.ToString("D"));
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"the Core refused checkpoint '{name}' ({(int)response.StatusCode}): {body}");
        return body;
    }
}

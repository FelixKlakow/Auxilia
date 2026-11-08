using System.Text.Json;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Reads a run stream's SSE body one event at a time (keepalives skipped), so a test can publish
/// the next transition only after the previous frame arrived.
/// </summary>
internal sealed class SseFrames(Stream body) : IDisposable
{
    private readonly StreamReader _reader = new(body);

    /// <summary>The <c>id:</c> of the last frame read.</summary>
    public string? LastId { get; private set; }

    /// <summary>The next frame, or null when the server closed the stream.</summary>
    public async Task<RunStreamEvent?> NextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        string? id = null;
        while (await _reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
                id = line["id: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                LastId = id;
                return JsonSerializer.Deserialize<RunStreamEvent>(line["data: ".Length..], JsonSerializerOptions.Web);
            }
        }
        return null;
    }

    public static string? StateOf(RunStreamEvent? frame)
        => frame is { Kind: RunStreamEvent.StatusKind }
            ? JsonSerializer.Deserialize<Auxilia.Workflows.Messaging.Messages.WorkflowStatusEvent>(
                frame.PayloadJson, JsonSerializerOptions.Web)?.State
            : null;

    public void Dispose() => _reader.Dispose();
}

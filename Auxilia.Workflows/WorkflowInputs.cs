using System.Threading.Channels;

namespace Auxilia.Workflows;

/// <summary>
/// Inputs delivered into this running instance (the steer-back half of the steering loop):
/// opaque payloads an authorized client posted to the Core's deliver-input endpoint, forwarded
/// to the instance's response queue. The platform never interprets them — the workflow's own
/// codec does (e.g. a steering decision protocol).
/// </summary>
public interface IWorkflowInputs
{
    /// <summary>Waits for the next delivered input payload.</summary>
    Task<string> ReceiveAsync(CancellationToken ct = default);
}

/// <summary>Channel-backed <see cref="IWorkflowInputs"/> fed by the response-queue subscription.</summary>
internal sealed class ChannelWorkflowInputs : IWorkflowInputs
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();

    public void Push(string payloadJson) => _channel.Writer.TryWrite(payloadJson);

    public async Task<string> ReceiveAsync(CancellationToken ct = default)
        => await _channel.Reader.ReadAsync(ct);
}

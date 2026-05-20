using Auxilia.Workflows;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakeSignalEmitter : ISignalEmitter
{
    public List<(string SignalName, object Payload)> EmittedSignals { get; } = new();

    public Task EmitAsync<TPayload>(string signalName, TPayload payload, CancellationToken cancellationToken = default)
    {
        EmittedSignals.Add((signalName, payload!));
        return Task.CompletedTask;
    }
}

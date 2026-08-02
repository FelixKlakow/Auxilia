namespace Auxilia.Workflows;

public interface ISignalEmitter
{
    Task EmitAsync<TPayload>(string signalName, TPayload payload,
        CancellationToken cancellationToken = default);
}

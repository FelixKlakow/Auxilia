using Auxilia.Workflows.AiAgent;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakeAiAgent : IAiAgent
{
    private readonly Queue<IReadOnlyList<ScriptedTurn>> _sessionTurns;
    private int _remainingInitialFailures;

    public FakeAiAgent(Queue<IReadOnlyList<ScriptedTurn>> sessionTurns, int initialFailCount = 0)
    {
        _sessionTurns = sessionTurns;
        _remainingInitialFailures = initialFailCount;
    }

    public int OpenSessionCallCount { get; private set; }
    public int InitialFailCount { get; set; }

    public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_remainingInitialFailures > 0)
        {
            _remainingInitialFailures--;
            throw new InvalidOperationException("Scripted transient AI provider failure.");
        }

        if (_sessionTurns.Count == 0)
            throw new InvalidOperationException("Transcript is exhausted. No more scripted turns available.");

        OpenSessionCallCount++;
        return Task.FromResult<IAiSession>(new FakeAiSession(_sessionTurns.Dequeue()));
    }
}

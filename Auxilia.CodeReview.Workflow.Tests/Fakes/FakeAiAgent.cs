using Auxilia.Workflows.AiAgent;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class FakeAiAgent : IAiAgent
{
    private readonly Queue<IReadOnlyList<ScriptedTurn>> _sessionTurns;
    private int _remainingInitialFailures;

    public FakeAiAgent(Queue<ScriptedTurn> transcript, int initialFailCount = 0)
    {
        var sessions = new Queue<IReadOnlyList<ScriptedTurn>>();
        foreach (var turn in transcript)
            sessions.Enqueue(new[] { turn });
        _sessionTurns = sessions;
        _remainingInitialFailures = initialFailCount;
    }

    public FakeAiAgent(Queue<IReadOnlyList<ScriptedTurn>> sessionTurns, int initialFailCount = 0)
    {
        _sessionTurns = sessionTurns;
        _remainingInitialFailures = initialFailCount;
    }

    public int OpenSessionCallCount { get; private set; }
    public AiSessionOptions? LastSessionOptions { get; private set; }

    public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_remainingInitialFailures > 0)
        {
            _remainingInitialFailures--;
            throw new InvalidOperationException("Scripted transient AI provider failure.");
        }

        if (_sessionTurns.Count == 0)
            throw new InvalidOperationException("Transcript is exhausted. No more scripted turns available.");

        LastSessionOptions = options;
        OpenSessionCallCount++;
        return Task.FromResult<IAiSession>(new FakeAiSession(_sessionTurns.Dequeue()));
    }
}

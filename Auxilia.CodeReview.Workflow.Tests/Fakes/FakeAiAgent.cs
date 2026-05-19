using Auxilia.Workflows.AiAgent;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class FakeAiAgent : IAiAgent
{
    private readonly Queue<IReadOnlyList<ScriptedTurn>> _sessionTurns;

    public FakeAiAgent(Queue<ScriptedTurn> transcript)
    {
        var sessions = new Queue<IReadOnlyList<ScriptedTurn>>();
        foreach (var turn in transcript)
            sessions.Enqueue(new[] { turn });
        _sessionTurns = sessions;
    }

    public FakeAiAgent(Queue<IReadOnlyList<ScriptedTurn>> sessionTurns)
    {
        _sessionTurns = sessionTurns;
    }

    public int OpenSessionCallCount { get; private set; }

    public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_sessionTurns.Count == 0)
            throw new InvalidOperationException("Transcript is exhausted. No more scripted turns available.");

        OpenSessionCallCount++;
        return Task.FromResult<IAiSession>(new FakeAiSession(_sessionTurns.Dequeue()));
    }
}

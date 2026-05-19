using Auxilia.Workflows.AiAgent;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class FakeAiAgent : IAiAgent
{
    private readonly Queue<ScriptedTurn> _transcript;

    public FakeAiAgent(Queue<ScriptedTurn> transcript)
    {
        _transcript = transcript;
    }

    public int OpenSessionCallCount { get; private set; }

    public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_transcript.Count == 0)
            throw new InvalidOperationException("Transcript is exhausted. No more scripted turns available.");

        OpenSessionCallCount++;
        return Task.FromResult<IAiSession>(new FakeAiSession(_transcript.Dequeue()));
    }
}

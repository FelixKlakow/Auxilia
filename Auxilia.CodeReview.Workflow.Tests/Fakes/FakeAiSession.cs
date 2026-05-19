using Auxilia.Workflows.AiAgent;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class FakeAiSession : IAiSession
{
    private readonly ScriptedTurn _turn;

    public FakeAiSession(ScriptedTurn turn)
    {
        _turn = turn;
    }

    public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (!prompt.Contains(_turn.ExpectedPromptSubstring))
            throw new InvalidOperationException(
                $"Prompt mismatch. Expected substring: '{_turn.ExpectedPromptSubstring}'. Actual prompt: '{prompt}'");

        return Task.FromResult(_turn.Response);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

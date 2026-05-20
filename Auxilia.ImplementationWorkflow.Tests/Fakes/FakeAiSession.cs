using Auxilia.Workflows.AiAgent;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakeAiSession : IAiSession
{
    private readonly IReadOnlyList<ScriptedTurn> _turns;

    public FakeAiSession(IReadOnlyList<ScriptedTurn> turns)
    {
        _turns = turns;
    }

    public async Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        foreach (var turn in _turns)
        {
            if (turn.ExpectedPromptSubstring == "" || prompt.Contains(turn.ExpectedPromptSubstring))
            {
                if (turn.ToolCall is not null)
                    await turn.ToolCall();

                return turn.Response;
            }
        }

        var available = string.Join(", ", _turns.Select(t => $"'{t.ExpectedPromptSubstring}'"));
        throw new InvalidOperationException(
            $"No scripted turn matched prompt. Available substrings: [{available}]. Prompt: '{prompt}'");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed record ScriptedTurn(
    string ExpectedPromptSubstring,
    string Response,
    Func<Task>? ToolCall = null);

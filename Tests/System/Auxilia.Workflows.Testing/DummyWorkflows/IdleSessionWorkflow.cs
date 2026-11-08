using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.Steering;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Dummy workflow for multi-turn sessions that end by themselves: the real session engine
/// (<see cref="AgentSessionApplication"/>) around a scripted agent that answers each turn at once
/// and then waits for the next instruction like the providers do — no model, no credentials. Run
/// it multi-turn with an <c>idle-timeout</c> (or none, then the platform's maximum applies).
/// </summary>
public static class IdleSessionWorkflow
{
    public const string WorkflowName = "idle-session-workflow";

    /// <summary>Schema marker of the steering view — its items are protocol JSON.</summary>
    private sealed record SteeringWireItem;

    public static Task RunAsync(string[] args) =>
        WorkflowBuilder
            .Create(WorkflowName)
            .DeclaresView<AgentChatEntry>(AgentSessionApplication.ChatViewName, ViewRendering.Custom,
                ViewLifecycle.LiveAndPersisted)
            .DeclaresView<SessionProgressEntry>(AgentSessionApplication.ProgressViewName, ViewRendering.Log,
                ViewLifecycle.LiveAndPersisted)
            .DeclaresView<SteeringWireItem>(OperatorChannel.ViewName, ViewRendering.Custom,
                ViewLifecycle.LiveAndPersisted)
            .WithApplication((provider, ct) => new AgentSessionApplication(
                    new ScriptedTurnAgent(),
                    provider.GetService<IViewPublisher>(),
                    AgentSessionContext.FromEnvironment(),
                    TimeProvider.System,
                    provider.GetService<IWorkflowInputs>(),
                    provider.GetService<IRunOutcome>())
                .RunAsync(ct))
            .Run(args);

    /// <summary>Answers every turn immediately; the next guidance is the next turn, the end token ends it.</summary>
    private sealed class ScriptedTurnAgent : ICodingAgent
    {
        public async Task<CodingAgentResult> RunAsync(
            CodingAgentRequest request, Func<AgentChatEntry, CancellationToken, Task> onChatEntry,
            CancellationToken cancellationToken = default)
        {
            var turns = 0;
            var prompt = request.Instruction is { Length: > 0 } instruction ? instruction : null;
            while (true)
            {
                if (prompt is not null)
                {
                    turns++;
                    await onChatEntry(new AgentChatEntry(
                        AgentChatRole.Assistant, $"turn {turns}: {prompt}", DateTimeOffset.UtcNow), cancellationToken);
                    if (request.OnTurnEnded is { } turnEnded)
                        await turnEnded(turns, $"turn {turns} done", cancellationToken);
                }

                using var either = CancellationTokenSource.CreateLinkedTokenSource(request.EndToken, cancellationToken);
                try
                {
                    prompt = await request.Interaction!.WaitForGuidanceAsync(either.Token);
                }
                catch (OperationCanceledException) when (request.EndToken.IsCancellationRequested)
                {
                    return new CodingAgentResult(true, $"ended after {turns} turn(s)", turns);
                }
            }
        }
    }
}

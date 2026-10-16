using Auxilia.Workflows.Actions;
using Auxilia.Workflows.Views;

namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// An agent session's PER-ACTION policies, enforced where they cannot be talked around: at the
/// enforcement points (push gateway, …) against what each action concretely does. The session
/// maps an action kind to its policy (push → push policy; any other kind → the session's
/// permission mode); auto-allow allows and records, ask-operator asks the session's operator
/// with the action's details — and denies when there is no operator to ask. Every decision
/// lands in the session's chat.
/// </summary>
public sealed class AgentSessionActionApprover(
    Func<string, string> policyFor,
    IAgentInteraction? interaction,
    Func<AgentChatEntry, CancellationToken, Task> onChatEntry,
    TimeProvider? time = null) : IActionApprover
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<ActionDecision> DecideAsync(GatedAction action, CancellationToken cancellationToken)
    {
        if (string.Equals(policyFor(action.Kind), AgentPermissionModes.AutoAllow, StringComparison.OrdinalIgnoreCase))
        {
            await RecordAsync($"Allowed automatically ({action.Kind}, policy: auto-allow): {action.Summary}.", cancellationToken);
            return ActionDecision.Allow;
        }

        if (interaction is null)
        {
            var denied = ActionDecision.Deny($"the {action.Kind} policy is ask-operator but this session has no operator to ask");
            await RecordAsync($"DENIED ({action.Kind}) — {denied.Reason}: {action.Summary}.", cancellationToken);
            return denied;
        }

        var answer = await interaction.AskAsync(new AgentQuestion(
            Prompt(action),
            [new AgentQuestionOption("allow", "Allow"), new AgentQuestionOption("deny", "Deny")],
            MultiSelect: false, AllowFreeText: false,
            Detail: string.Join("\n", action.Details.DefaultIfEmpty(action.Summary)) + $"\n\ntarget: {action.Target}"),
            cancellationToken);
        var allowed = string.Equals(answer.SelectedIds.FirstOrDefault(), "allow", StringComparison.OrdinalIgnoreCase);
        await RecordAsync(
            $"The operator {(allowed ? "allowed" : "DENIED")} {action.Kind}: {action.Summary}.", cancellationToken);
        return allowed ? ActionDecision.Allow : ActionDecision.Deny("the operator declined");
    }

    private static string Prompt(GatedAction action)
        => action.Kind == ActionKinds.GitPush
            ? "The agent wants to PUSH to the remote repository. Allow it?"
            : $"The agent wants to perform '{action.Kind}' on {action.Target}. Allow it?";

    private Task RecordAsync(string text, CancellationToken ct)
        => onChatEntry(new AgentChatEntry(
            AgentChatRole.System, text, _time.GetUtcNow(), Label: "Action policy", DetailTag: "permissions"), ct);
}

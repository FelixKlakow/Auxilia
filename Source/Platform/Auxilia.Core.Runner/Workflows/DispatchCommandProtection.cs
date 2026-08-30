using Auxilia.PlatformData.Protection;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// At-rest protection of the persisted dispatch command's <c>ResolutionToken</c> — the run's
/// bearer capability against the Core's slot-resolution endpoint. <see cref="Protect"/> swaps
/// the plaintext for the protector's ciphertext before the command is serialized into
/// <c>WorkflowInstanceRecord.DispatchCommandJson</c>; <see cref="Unprotect"/> restores it on
/// every read path that needs the live token. Uses the same configuration-keyed
/// <see cref="ISettingsProtector"/> as the record's protected instance token, so a restarted
/// runner keeps decrypting (re-adoption) exactly like it does for that token.
/// </summary>
internal static class DispatchCommandProtection
{
    public static RunWorkflowCommand Protect(RunWorkflowCommand command, ISettingsProtector protector)
        => command.ResolutionToken is { Length: > 0 } token
            ? command with { ResolutionToken = protector.Protect(token) }
            : command;

    /// <summary>
    /// Restores the plaintext resolution token. A protection-key change makes the stored token
    /// unrecoverable (same failure mode as the instance token): the token is dropped so readers
    /// fail visibly at credential resolution instead of relaying an unusable value.
    /// </summary>
    public static RunWorkflowCommand Unprotect(
        RunWorkflowCommand command, ISettingsProtector protector, ILogger logger)
    {
        if (command.ResolutionToken is not { Length: > 0 } token)
            return command;
        try
        {
            return command with { ResolutionToken = protector.Unprotect(token) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Unprotecting the stored resolution token of command {CommandId} failed — treating the run as having none.",
                command.CommandId);
            return command with { ResolutionToken = null };
        }
    }
}

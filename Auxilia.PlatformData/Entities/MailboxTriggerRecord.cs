using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// Mailbox trigger of a workflow configuration: the adapter polls the referenced email slot
/// instance's mailbox and dispatches one run per unseen mail. Credentials live only on the
/// slot instance — the trigger is a reference plus a poll cadence.
/// </summary>
public sealed record MailboxTriggerRecord : IEntity
{
    public Guid Id { get; init; }
    public required Guid WorkflowConfigurationId { get; init; }
    /// <summary>The email slot instance whose IMAP settings this trigger polls.</summary>
    public required Guid SlotInstanceId { get; init; }
    public int PollIntervalSeconds { get; init; } = 15;
    public bool Enabled { get; init; } = true;
    /// <summary>Principal on whose behalf mail-triggered dispatches run (policy-checked).</summary>
    public Guid? RunAsPrincipalId { get; init; }
}

namespace Auxilia.Adapters.Email;

/// <summary>
/// Configuration of one watched mailbox. Gmail is a configuration of this adapter
/// (imap.gmail.com:993, SSL, app password) — not a separate implementation.
/// </summary>
public sealed class EmailTaskSourceSettings
{
    /// <summary>Disabled by default — enable per deployment when a mailbox is configured.</summary>
    public bool Enabled { get; set; }

    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Folder { get; set; } = "INBOX";

    /// <summary>SMTP endpoint for reply write-back; empty disables replies.</summary>
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;

    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>Workflow dispatched once per incoming mail.</summary>
    public string WorkflowType { get; set; } = string.Empty;
    public string WorkflowPackageUri { get; set; } = string.Empty;

    /// <summary>Principal on whose behalf mail-triggered dispatches run (policy-checked).</summary>
    public Guid? RunAsPrincipalId { get; set; }

    /// <summary>Dispatch command queue consumed by the Steering Instance pool.</summary>
    public string CommandQueueName { get; set; } = "workflow.run-commands";
}

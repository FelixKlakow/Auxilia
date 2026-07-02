namespace Auxilia.Adapters.Email;

/// <summary>
/// Connection settings of one mailbox. Gmail is a configuration of this shape
/// (imap.gmail.com:993, SSL, app password) — not a separate implementation. Which mailboxes
/// exist is platform data (email slot instances), not deployment configuration.
/// </summary>
public sealed class EmailTaskSourceSettings
{
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Folder { get; set; } = "INBOX";

    /// <summary>SMTP endpoint for reply write-back; empty disables replies.</summary>
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
}

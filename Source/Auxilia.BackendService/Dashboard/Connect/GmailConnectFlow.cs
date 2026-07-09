namespace Auxilia.BackendService.Dashboard.Connect;

/// <summary>
/// "Connect Gmail account": Google exposes no OAuth flow without an app registration, so
/// this flow guides the user to Google's app-password page and takes the generated password
/// as the credential (IMAP/SMTP accept app passwords like the account password). The value
/// is returned to the caller — this class never stores or logs it.
/// </summary>
public sealed class GmailConnectFlow : IConnectFlow
{
    public const string FlowKey = "google-gmail";

    public string Key => FlowKey;
    public string DisplayName => "Gmail account";
    public string Instructions
        => "Sign in on the Google page (2-step verification required), create an app password "
           + "for 'Mail', and paste it back here.";

    public Task<ConnectStart> BeginAsync(CancellationToken ct = default)
        => Task.FromResult(new ConnectStart(
            "https://myaccount.google.com/apppasswords",
            State: Guid.NewGuid().ToString("N"),
            PasteRequired: true));

    public Task<string> CompleteAsync(string state, string? pastedCode, CancellationToken ct = default)
    {
        // Google displays app passwords in groups ("abcd efgh ijkl mnop") — strip the spaces.
        var password = new string((pastedCode ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (password.Length == 0)
            throw new ConnectPendingException("Paste the app password Google shows you.");
        return Task.FromResult(password);
    }
}

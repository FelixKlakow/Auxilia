using System.Net.Mail;
using Auxilia.Core.Contracts;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Approval-pipeline handler "email": notifies the signing authority that a workflow-type
/// registration awaits a decision, then defers — it never decides itself. Unconfigured SMTP
/// (or a send failure) also defers: notification is best-effort, the pending state is the gate.
/// </summary>
public sealed class EmailApprovalNotificationHandler(
    IOptions<CoreApiSettings> settings,
    ILogger<EmailApprovalNotificationHandler> logger) : IWorkflowTypeApprovalHandler
{
    public string Name => "email";

    public async Task<ApprovalHandlerResult> EvaluateAsync(
        WorkflowTypeRegistrationDto registration, CancellationToken ct)
    {
        var email = settings.Value.ApprovalEmail;
        if (string.IsNullOrWhiteSpace(email.Host) || string.IsNullOrWhiteSpace(email.To))
        {
            logger.LogDebug("Approval email is not configured — deferring without notification.");
            return ApprovalHandlerResult.Deferred;
        }

        try
        {
            using var client = new SmtpClient(email.Host, email.Port) { EnableSsl = email.UseSsl };
            using var message = new MailMessage(
                email.From is { Length: > 0 } from ? from : "auxilia-core@localhost",
                email.To,
                $"[Auxilia] Workflow type '{registration.WorkflowType}' awaits signing",
                $"A workflow-type registration is pending the signing authority's decision.\n\n" +
                $"Type:      {registration.WorkflowType}\n" +
                $"Package:   {registration.PackageUri}\n" +
                $"Publisher: {registration.PublisherKeyBase64 ?? "(no signature — docker package)"}\n" +
                $"Registered {registration.RegisteredUtc:u} by {registration.RegisteredBy?.ToString() ?? "unknown"}\n\n" +
                "Approve or deny it in the Core (POST /api/workflow-types/{type}/approve | /deny).");
            await client.SendMailAsync(message, ct);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException)
        {
            logger.LogWarning(ex, "Could not send the approval notification email — deferring anyway.");
        }

        return ApprovalHandlerResult.Deferred;
    }
}

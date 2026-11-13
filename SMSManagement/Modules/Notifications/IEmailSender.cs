namespace SMSManagement.Modules.Notifications;

/// <summary>
/// Fire-and-forget email send. Never throws for a delivery failure — alerting
/// must not abort the business operation that triggered it. The outcome is
/// recorded in <see cref="OutboundEmail"/>; use <see cref="IEmailDispatcher"/>
/// when the caller needs to see it (the admin test button).
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <param name="Category">Which feature is sending — shown in the email log
/// and passed to the relay as a tag. Optional so existing callers compile.</param>
public sealed record EmailMessage(
    IReadOnlyList<string> To,
    string Subject,
    string HtmlBody,
    string? PlainTextBody = null,
    IReadOnlyList<EmailAttachment>? Attachments = null,
    string? Category = null);

/// <summary>A file attached to an outbound email (e.g. an SMS-round log CSV).</summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

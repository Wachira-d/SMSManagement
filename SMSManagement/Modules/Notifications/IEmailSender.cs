namespace SMSManagement.Modules.Notifications;

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public sealed record EmailMessage(
    IReadOnlyList<string> To,
    string Subject,
    string HtmlBody,
    string? PlainTextBody = null,
    IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>A file attached to an outbound email (e.g. an SMS-round log CSV).</summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

namespace SMSManagement.Modules.Notifications;

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public sealed record EmailMessage(
    IReadOnlyList<string> To,
    string Subject,
    string HtmlBody,
    string? PlainTextBody = null);

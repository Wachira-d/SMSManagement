using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace SMSManagement.Modules.Notifications;

/// <summary>
/// Plain System.Net.Mail transport. Three modes:
///   1. Host configured     => real SMTP delivery.
///   2. PickupDirectory set => write .eml files locally (dev).
///   3. Neither             => Skipped, with a warning — missing email
///      config must not block business operations.
///
/// SMTP hands the message over synchronously, so a successful call is the
/// final answer (Sent). There is no later delivery receipt to wait for.
/// </summary>
public sealed class SmtpEmailTransport : IEmailTransport
{
    private readonly IOptionsMonitor<SmtpOptions> _optsMonitor;
    private readonly ILogger<SmtpEmailTransport> _log;

    public SmtpEmailTransport(IOptionsMonitor<SmtpOptions> opts, ILogger<SmtpEmailTransport> log)
    {
        _optsMonitor = opts;
        _log = log;
    }

    public string Name => EmailOptions.ProviderSmtp;

    public async Task<EmailSendResult> SendAsync(
        EmailMessage message, string idempotencyKey, CancellationToken ct)
    {
        // Re-read on every send so a settings change applies without restart.
        var opts = _optsMonitor.CurrentValue;

        if (string.IsNullOrWhiteSpace(opts.Host) && string.IsNullOrWhiteSpace(opts.PickupDirectory))
        {
            _log.LogWarning(
                "Email send skipped — Smtp:Host and PickupDirectory both blank. Subject={Subject}",
                message.Subject);
            return EmailSendResult.Skip("SMTP is not configured (Smtp:Host and PickupDirectory are both blank).");
        }

        try
        {
            using var mail = new MailMessage
            {
                From = new MailAddress(opts.FromAddress, opts.FromDisplayName),
                Subject = message.Subject,
                Body = message.HtmlBody,
                IsBodyHtml = true,
            };
            if (!string.IsNullOrEmpty(message.PlainTextBody))
            {
                // Prefer multipart/alternative so clients that strip HTML still render something.
                mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                    message.PlainTextBody, null, "text/plain"));
                mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                    message.HtmlBody, null, "text/html"));
                mail.Body = string.Empty;
                mail.IsBodyHtml = false;
            }
            foreach (var addr in message.To)
                mail.To.Add(addr);

            if (message.Attachments is { Count: > 0 })
                foreach (var a in message.Attachments)
                    // The Attachment owns the stream; MailMessage disposal frees it.
                    mail.Attachments.Add(new Attachment(
                        new MemoryStream(a.Content), a.FileName, a.ContentType));

            using var client = new SmtpClient();
            if (!string.IsNullOrWhiteSpace(opts.PickupDirectory))
            {
                Directory.CreateDirectory(opts.PickupDirectory);
                client.DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory;
                client.PickupDirectoryLocation = opts.PickupDirectory;
            }
            else
            {
                client.Host = opts.Host;
                client.Port = opts.Port;
                client.EnableSsl = opts.EnableSsl;
                if (!string.IsNullOrEmpty(opts.Username))
                    client.Credentials = new NetworkCredential(opts.Username, opts.Password);
            }

            await client.SendMailAsync(mail, ct);
            _log.LogInformation("Email sent via SMTP to {Recipients} subject={Subject}",
                string.Join(",", message.To.Select(EmailMasking.Mask)), message.Subject);
            return new EmailSendResult(OutboundEmailStatus.Sent, SentAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Building the MailMessage can throw too (a malformed From or To
            // address), so it sits inside the try with the send itself.
            _log.LogError(ex, "Email send FAILED via SMTP subject={Subject}", message.Subject);
            return new EmailSendResult(OutboundEmailStatus.Failed, Error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

internal static class EmailMasking
{
    /// <summary><c>alice@example.com</c> → <c>al***@example.com</c> for logs.</summary>
    public static string Mask(string addr)
    {
        var at = addr.IndexOf('@');
        if (at <= 1) return "***";
        return addr[..2] + "***" + addr[at..];
    }
}

using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace SMSManagement.Modules.Notifications;

/// <summary>
/// Plain System.Net.Mail-based sender. For higher-fidelity delivery
/// (DKIM, MIME tweaks, OAuth) swap in MailKit by re-registering this
/// interface in DI — no caller change.
///
/// Three modes:
///   1. Host configured        => real SMTP delivery.
///   2. PickupDirectory set    => write .eml files locally (dev).
///   3. Neither                => log a warning and no-op (the spec
///      explicitly requires that missing email config does NOT block
///      business operations).
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _opts;
    private readonly ILogger<SmtpEmailSender> _log;

    public SmtpEmailSender(IOptions<SmtpOptions> opts, ILogger<SmtpEmailSender> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (message.To.Count == 0) return;

        if (string.IsNullOrWhiteSpace(_opts.Host) && string.IsNullOrWhiteSpace(_opts.PickupDirectory))
        {
            _log.LogWarning(
                "Email send skipped — Smtp:Host and PickupDirectory both blank. Subject={Subject}",
                message.Subject);
            return;
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(_opts.FromAddress, _opts.FromDisplayName),
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

        using var client = new SmtpClient();
        if (!string.IsNullOrWhiteSpace(_opts.PickupDirectory))
        {
            Directory.CreateDirectory(_opts.PickupDirectory);
            client.DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory;
            client.PickupDirectoryLocation = _opts.PickupDirectory;
        }
        else
        {
            client.Host = _opts.Host;
            client.Port = _opts.Port;
            client.EnableSsl = _opts.EnableSsl;
            if (!string.IsNullOrEmpty(_opts.Username))
                client.Credentials = new NetworkCredential(_opts.Username, _opts.Password);
        }

        try
        {
            await client.SendMailAsync(mail, ct);
            _log.LogInformation("Email sent to {Recipients} subject={Subject}",
                string.Join(",", message.To.Select(MaskEmail)), message.Subject);
        }
        catch (Exception ex)
        {
            // Don't propagate — alerting failure must never abort the business txn.
            _log.LogError(ex, "Email send FAILED subject={Subject}", message.Subject);
        }
    }

    private static string MaskEmail(string addr)
    {
        var at = addr.IndexOf('@');
        if (at <= 1) return "***";
        return addr[..2] + "***" + addr[at..];
    }
}

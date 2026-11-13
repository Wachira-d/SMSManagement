using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SMSManagement.Modules.Notifications.Relay;

/// <summary>
/// Sends through EmailRelayAPI's <c>POST /api/v1/emails</c>. The relay queues
/// and delivers asynchronously, so a successful call normally ends in
/// <see cref="OutboundEmailStatus.Queued"/>; the final Sent/Failed arrives
/// later by webhook (or the reconciler's pull).
/// </summary>
public sealed class EmailRelayTransport : IEmailTransport
{
    private readonly EmailRelayClient _client;
    private readonly IOptionsMonitor<EmailRelayOptions> _opts;
    private readonly ILogger<EmailRelayTransport> _log;

    public EmailRelayTransport(EmailRelayClient client, IOptionsMonitor<EmailRelayOptions> opts,
        ILogger<EmailRelayTransport> log)
    {
        _client = client;
        _opts = opts;
        _log = log;
    }

    public string Name => EmailOptions.ProviderRelay;

    /// <summary>The JSON body, built separately so tests can pin it.</summary>
    internal static object BuildPayload(EmailMessage m, string idempotencyKey, EmailRelayOptions o)
    {
        // The relay stores tags comma-joined, so a comma would split one tag in two.
        var tags = new List<string> { "sms-management" };
        if (!string.IsNullOrWhiteSpace(m.Category)) tags.Add(m.Category!.Replace(',', '-').Trim());

        object? from = string.IsNullOrWhiteSpace(o.FromAddress)
            ? null
            : new { address = o.FromAddress!.Trim(), name = string.IsNullOrWhiteSpace(o.FromName) ? null : o.FromName!.Trim() };

        return new
        {
            to = m.To.Select(a => new { address = a }).ToArray(),
            subject = m.Subject,
            html = m.HtmlBody,
            text = string.IsNullOrEmpty(m.PlainTextBody) ? null : m.PlainTextBody,
            from,
            senderProfile = string.IsNullOrWhiteSpace(o.SenderProfile) ? null : o.SenderProfile!.Trim(),
            attachments = m.Attachments is { Count: > 0 }
                ? m.Attachments.Select(a => new
                {
                    fileName = a.FileName,
                    contentType = a.ContentType,
                    contentBase64 = Convert.ToBase64String(a.Content)
                }).ToArray()
                : null,
            tags,
            idempotencyKey
        };
    }

    public async Task<EmailSendResult> SendAsync(EmailMessage message, string idempotencyKey, CancellationToken ct)
    {
        if (!_client.IsConfigured(out var problem))
        {
            _log.LogWarning("Email send skipped — relay not configured: {Problem}. Subject={Subject}",
                problem, message.Subject);
            return EmailSendResult.Skip(problem!);
        }

        var payload = BuildPayload(message, idempotencyKey, _opts.CurrentValue);
        RelayResponse resp;
        try
        {
            resp = await _client.SendAsync(payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Never reached the relay (DNS, TLS, timeout). The idempotency key
            // makes a later manual resend safe even if it actually got there.
            _log.LogError(ex, "Email relay unreachable subject={Subject}", message.Subject);
            // A timeout can hide a send the relay did accept. There's no relay
            // id to pull, but its webhook still matches by idempotency key and
            // would flip this to Sent — say so rather than claim a sure failure.
            var ambiguous = ex is TaskCanceledException or OperationCanceledException or TimeoutException;
            return new EmailSendResult(OutboundEmailStatus.Failed, Error: $"{ex.GetType().Name}: {ex.Message}"
                + (ambiguous ? " — no answer from the relay; it may still have accepted the email." : string.Empty));
        }

        if (resp.IsSuccessStatus && resp.Data is { } data)
        {
            // 202 = accepted; 200 + duplicate:true = this idempotency key was
            // already accepted, and data carries its current status.
            var id = EmailRelayClient.Str(data, "id");
            var status = RelayStatus.Parse(EmailRelayClient.Str(data, "status")) ?? OutboundEmailStatus.Queued;
            _log.LogInformation("Email accepted by relay id={RelayId} status={Status} to={Recipients} subject={Subject}",
                id, status, string.Join(",", message.To.Select(EmailMasking.Mask)), message.Subject);
            return new EmailSendResult(status, RelayMessageId: id,
                SentAt: status == OutboundEmailStatus.Sent ? DateTimeOffset.UtcNow : null);
        }

        var code = (int)resp.StatusCode;
        // 4xx (bad payload, From not allowed, quota, bad key) won't succeed on
        // retry without a config change → Rejected. 429 is the exception: the
        // relay is fine, we were just too fast.
        var rejected = code is >= 400 and < 500 && resp.StatusCode != HttpStatusCode.TooManyRequests;
        _log.LogError("Email relay refused send: {Detail} subject={Subject}", resp.Describe(), message.Subject);
        return new EmailSendResult(rejected ? OutboundEmailStatus.Rejected : OutboundEmailStatus.Failed,
            Error: Truncate(resp.Describe(), 1000));
    }

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Reads the relay's <c>GET /api/v1/emails/{id}</c> data object.</summary>
internal static class RelayMessageView
{
    public static RelayStatus.Update? ToUpdate(JsonElement data)
    {
        var status = RelayStatus.Parse(EmailRelayClient.Str(data, "status"));
        if (status is null) return null;
        int? attempts = data.TryGetProperty("attempts", out var a) && a.ValueKind == JsonValueKind.Number
            && a.TryGetInt32(out var n) ? n : null;
        return new RelayStatus.Update(
            status.Value,
            attempts,
            EmailRelayClient.Str(data, "providerMessageId"),
            EmailRelayClient.Str(data, "lastError") ?? EmailRelayClient.Str(data, "error"),
            ParseDate(EmailRelayClient.Str(data, "sentAt")));
    }

    /// <summary>The relay stores UTC but its SQL Server column round-trips
    /// without an offset, so a date with no Z/offset is taken as UTC — not
    /// local time, which on a +07:00 host would shift it seven hours.</summary>
    public static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var d) ? d : null;
    }
}

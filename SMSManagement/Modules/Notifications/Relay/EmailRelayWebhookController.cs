using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Notifications.Relay;

/// <summary>
/// Delivery-status callback from EmailRelayAPI. Set the relay client's
/// Webhook URL to <c>https://&lt;this host&gt;/api/email/relay/webhook</c> and its
/// Webhook secret to the same value as <c>Email:Relay:WebhookSecret</c>.
///
/// The relay POSTs <c>email.sent</c> / <c>email.failed</c>, signed
/// <c>sha256=hex(HMAC(secret, ts + "." + body))</c>, up to three times, and
/// stops retrying on any 4xx except 408/429. So: 401 for a bad signature (we
/// want it to stop), 200 for a message we don't know (retrying won't help),
/// and every call — accepted or not — is written to <see cref="EmailWebhookLog"/>.
/// </summary>
[ApiController]
[Route("api/email/relay/webhook")]
public sealed class EmailRelayWebhookController : ControllerBase
{
    /// <summary>Same tolerance as the relay's own signed-request check.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(300);
    private const int MaxBodyBytes = 64 * 1024;

    private readonly AppDbContext _db;
    private readonly IOptionsMonitor<EmailRelayOptions> _opts;
    private readonly TimeProvider _clock;
    private readonly ILogger<EmailRelayWebhookController> _log;

    public EmailRelayWebhookController(AppDbContext db, IOptionsMonitor<EmailRelayOptions> opts,
        TimeProvider clock, ILogger<EmailRelayWebhookController> log)
    {
        _db = db;
        _opts = opts;
        _clock = clock;
        _log = log;
    }

    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        // Raw bytes, not model binding: the signature covers exactly what was
        // sent, and re-serialising would not reproduce it byte for byte.
        byte[] raw;
        using (var ms = new MemoryStream())
        {
            await Request.Body.CopyToAsync(ms, ct);
            raw = ms.ToArray();
        }
        var rawText = Encoding.UTF8.GetString(raw);
        var headerEvent = Header("X-Relay-Event");
        var ts = Header("X-Relay-Timestamp");
        var sig = Header("X-Relay-Signature");
        var o = _opts.CurrentValue;

        // ---- authenticate ----
        if (string.IsNullOrEmpty(sig))
        {
            if (!o.AllowUnsignedWebhooks)
            {
                await LogAsync("missing-signature", headerEvent, null, null, rawText,
                    "No X-Relay-Signature. Set the relay client's webhook secret, or (not recommended) allow unsigned webhooks.", ct);
                return Unauthorized(new { error = "missing signature" });
            }
        }
        else
        {
            if (string.IsNullOrEmpty(o.WebhookSecret))
            {
                await LogAsync("disabled", headerEvent, null, null, rawText,
                    "Signed webhook received but Email:Relay:WebhookSecret is not set here.", ct);
                return Unauthorized(new { error = "webhook secret not configured" });
            }
            if (!long.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
            {
                await LogAsync("stale-timestamp", headerEvent, null, null, rawText, "Missing or non-numeric X-Relay-Timestamp.", ct);
                return Unauthorized(new { error = "bad timestamp" });
            }
            if (!RelaySignature.VerifyWebhook(o.WebhookSecret, ts!, raw, sig))
            {
                await LogAsync("bad-signature", headerEvent, null, null, rawText,
                    "X-Relay-Signature does not match — the webhook secrets on the two sides differ.", ct);
                return Unauthorized(new { error = "bad signature" });
            }
            // Checked after the HMAC: the timestamp is part of what's signed, so
            // only a genuine relay call gets reported as a clock/replay problem.
            var skew = _clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(unix);
            if (skew.Duration() > MaxSkew)
            {
                await LogAsync("stale-timestamp", headerEvent, null, null, rawText,
                    $"Timestamp is {skew.TotalSeconds:F0}s off (limit ±{MaxSkew.TotalSeconds:F0}s) — replay, or a clock out of sync.", ct);
                return Unauthorized(new { error = "stale timestamp" });
            }
        }

        // ---- parse ----
        string? evt = headerEvent, relayId = null, idemKey = null;
        RelayStatus.Update? update = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            evt = EmailRelayClient.Str(root, "event") ?? headerEvent;
            relayId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            idemKey = EmailRelayClient.Str(root, "idempotencyKey");
            // The body's "status" is authoritative; the event name is
            // "email." + status, so it's a fallback for the same thing.
            update = RelayMessageView.ToUpdate(root);
            if (update is null && evt?.StartsWith("email.", StringComparison.Ordinal) == true
                && RelayStatus.Parse(evt["email.".Length..]) is { } fromEvent)
                update = new RelayStatus.Update(fromEvent);
        }
        catch (JsonException ex)
        {
            await LogAsync("parse-error", evt, null, null, rawText, ex.Message, ct);
            return BadRequest(new { error = "invalid JSON" });
        }

        if (update is null)
        {
            await LogAsync("parse-error", evt, relayId, null, rawText, "No recognisable status in payload.", ct);
            return BadRequest(new { error = "no status" });
        }

        // ---- match ----
        // By relay id first; by our idempotency key when the 202 response was
        // lost (timeout) and we never learned the relay id.
        OutboundEmail? row = null;
        if (!string.IsNullOrEmpty(relayId))
            row = await _db.OutboundEmails.FirstOrDefaultAsync(x => x.RelayMessageId == relayId, ct);
        if (row is null && !string.IsNullOrEmpty(idemKey))
            row = await _db.OutboundEmails.FirstOrDefaultAsync(x => x.IdempotencyKey == idemKey, ct);

        if (row is null)
        {
            // Another system sharing the relay client, or a row purged by
            // retention. 200 so the relay doesn't retry something that can't succeed.
            await LogAsync("unknown-message", evt, relayId, null, rawText, null, ct);
            return Ok(new { received = true, matched = false });
        }

        row.RelayMessageId ??= relayId;
        var outcome = RelayStatus.Apply(row, update with
        {
            Error = update.Error is null ? null : EmailRelayTransport.Truncate(update.Error, 2000)
        }, "webhook", _clock.GetUtcNow());

        _db.EmailWebhookLogs.Add(NewLog(outcome, evt, relayId, row.Id, rawText,
            outcome == "ignored-stale" ? $"Kept {row.Status}; relay reported {update.Status}." : null));
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Email relay webhook {Event} id={RelayId} → {Outcome} ({Status})",
            evt, relayId, outcome, row.Status);
        return Ok(new { received = true, matched = true, outcome, status = row.Status.ToString() });
    }

    private string? Header(string name)
        => Request.Headers.TryGetValue(name, out var v) && v.Count > 0 ? v[0] : null;

    private EmailWebhookLog NewLog(string outcome, string? evt, string? relayId, Guid? emailId, string raw, string? notes) => new()
    {
        CreatedAt = _clock.GetUtcNow(),
        Event = Cap(evt, 32),
        RelayMessageId = Cap(relayId, 64),
        OutboundEmailId = emailId,
        Outcome = outcome,
        RemoteIp = Cap(HttpContext.Connection.RemoteIpAddress?.ToString(), 64),
        RawPayload = Cap(raw, 4096),
        Notes = Cap(notes, 500),
    };

    /// <summary>Logs a rejected or unmatched call. A failure to write the log
    /// must not change the HTTP answer the relay gets.</summary>
    private async Task LogAsync(string outcome, string? evt, string? relayId, Guid? emailId, string raw,
        string? notes, CancellationToken ct)
    {
        _log.LogWarning("Email relay webhook {Outcome}: {Notes}", outcome, notes);
        try
        {
            _db.EmailWebhookLogs.Add(NewLog(outcome, evt, relayId, emailId, raw, notes));
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Could not write EmailWebhookLog ({Outcome})", outcome);
        }
    }

    private static string? Cap(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

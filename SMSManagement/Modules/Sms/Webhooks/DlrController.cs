using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Observability;
using SMSManagement.Modules.Sms.Domain;

namespace SMSManagement.Modules.Sms.Webhooks;

/// <summary>
/// Delivery-Receipt (DLR) ingress. Providers POST here when carrier
/// confirms (or rejects) a previously-dispatched message. Updates
/// SmsMessage.{Status, DeliveredAt, ErrorCode}.
///
/// Wire format differs per provider; both endpoints HMAC-verify the
/// raw body and then translate into the common SmsStatus enum.
/// </summary>
[ApiController]
[Route("api/sms/dlr")]
public sealed class DlrController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DlrWebhookOptions _secrets;
    private readonly TimeProvider _clock;
    private readonly CampaignMetrics _metrics;
    private readonly ILogger<DlrController> _log;

    public DlrController(
        AppDbContext db,
        IOptions<DlrWebhookOptions> secrets,
        TimeProvider clock,
        CampaignMetrics metrics,
        ILogger<DlrController> log)
    {
        _db = db;
        _secrets = secrets.Value;
        _clock = clock;
        _metrics = metrics;
        _log = log;
    }

    /// <summary>
    /// etracker (MACROKIOSK) delivery notification. Unlike the HMAC-signed
    /// Infobip webhook, etracker DN is an unsigned GET/POST carrying form or
    /// query params (msgID, msisdn, status, statusDetail). It is authenticated
    /// by a shared-secret token embedded in the DN URL configured on the
    /// etracker account: …/api/sms/dlr/etracker?token=THE_SECRET
    /// </summary>
    [HttpGet("etracker")]
    [HttpPost("etracker")]
    public async Task<IActionResult> Etracker(CancellationToken ct)
    {
        if (!TokenValid(_secrets.EtrackerDnToken))
        {
            _log.LogWarning("etracker DN rejected — missing or wrong token.");
            return Unauthorized();
        }

        var msgId = Param("msgID");
        var status = Param("status");
        var detail = Param("statusDetail");

        if (string.IsNullOrEmpty(msgId) || string.IsNullOrEmpty(status))
            return BadRequest("msgID and status are required.");

        var mapped = MapStatus(status);
        // Carry the failure reason into ErrorCode for non-delivered receipts.
        var code = mapped is SmsStatus.Failed or SmsStatus.Rejected or SmsStatus.Expired
            ? $"DN_{status.ToUpperInvariant()}"
              + (string.IsNullOrWhiteSpace(detail) ? "" : $": {detail}")
            : null;

        _log.LogInformation("etracker DN msgID={MsgId} status={Status} -> {Mapped}",
            msgId, status, mapped);
        await ApplyAsync("etracker", msgId, mapped, code, ct);
        return Ok();
    }

    [HttpPost("infobip")]
    public async Task<IActionResult> Infobip(CancellationToken ct)
    {
        if (!await VerifyAsync(_secrets.InfobipSecretBase64, ct)) return Unauthorized();
        Request.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);

        // Infobip DLR shape: { "results":[ { "messageId":"...", "status":{ "groupName":"DELIVERED" } } ] }
        if (!doc.RootElement.TryGetProperty("results", out var results)) return BadRequest("results array required.");
        foreach (var r in results.EnumerateArray())
        {
            var id = r.GetProperty("messageId").GetString();
            var group = r.GetProperty("status").GetProperty("groupName").GetString();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(group)) continue;
            await ApplyAsync("infobip", id, MapStatus(group), null, ct);
        }
        return Ok();
    }

    // ---- helpers ----

    /// <summary>Constant-time comparison of the URL <c>token</c> param against
    /// the configured etracker DN secret.</summary>
    private bool TokenValid(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return false;
        var supplied = Param("token");
        if (string.IsNullOrEmpty(supplied)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(configured));
    }

    /// <summary>Reads a parameter from the query string or, for POSTs, the
    /// form body — both are case-insensitive collections.</summary>
    private string? Param(string name)
    {
        if (Request.Query.TryGetValue(name, out var q) && !string.IsNullOrEmpty(q))
            return q.ToString();
        if (Request.HasFormContentType
            && Request.Form.TryGetValue(name, out var f) && !string.IsNullOrEmpty(f))
            return f.ToString();
        return null;
    }

    private async Task<bool> VerifyAsync(string? secret, CancellationToken ct)
    {
        // Buffer the body so we can both verify it and re-read it as JSON.
        Request.EnableBuffering();
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        Request.Body.Position = 0;

        var sig = Request.Headers["X-Signature"].ToString();
        var tsHeader = Request.Headers["X-Timestamp"].ToString();

        if (string.IsNullOrWhiteSpace(secret))
        {
            // Dev mode — secret not configured. Refuse unsigned requests anyway.
            _log.LogWarning("DLR HMAC secret missing; request rejected.");
            return false;
        }
        var ok = WebhookHmac.Verify(ms.ToArray(), secret, sig, tsHeader, _clock);
        if (!ok) _log.LogWarning("DLR HMAC verification failed.");
        return ok;
    }

    private async Task ApplyAsync(
        string provider, string providerMessageId, SmsStatus newStatus,
        string? errorCode, CancellationToken ct)
    {
        // IgnoreQueryFilters: the DLR webhook is an anonymous (HMAC-verified)
        // provider callback — it has no user context, so the project-scope
        // filter on SmsMessage would hide every message and silently drop
        // delivery receipts. Lookup is by provider + provider message id,
        // which the provider only knows for messages we actually sent.
        var msg = await _db.SmsMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.Provider == provider
                                   && m.ProviderMessageId == providerMessageId, ct);
        if (msg is null)
        {
            _log.LogInformation("DLR for unknown {Provider}/{Id} — ignored.",
                provider, providerMessageId);
            return;
        }

        // Delivered is terminal — a late ACCEPTED/PROCESSING receipt (DNs can
        // arrive out of order) must not downgrade it.
        if (msg.Status == SmsStatus.Delivered && newStatus != SmsStatus.Delivered)
        {
            _log.LogInformation("DLR {Status} for already-delivered {Provider}/{Id} — ignored.",
                newStatus, provider, providerMessageId);
            return;
        }

        msg.Status = newStatus;
        if (newStatus == SmsStatus.Delivered)
        {
            msg.DeliveredAt = _clock.GetUtcNow();
            _metrics.SmsDelivered.Add(1, KeyValuePair.Create<string, object?>("provider", provider));
        }
        else if (newStatus is SmsStatus.Failed or SmsStatus.Rejected)
        {
            _metrics.SmsFailed.Add(1, KeyValuePair.Create<string, object?>("provider", provider));
        }
        if (errorCode is not null) msg.ErrorCode = errorCode;
        await _db.SaveChangesAsync(ct);
    }

    // Common map across providers. etracker DN words: DELIVERED, UNDELIVERED,
    // ACCEPTED, PROCESSING (spec 4.3). Infobip uses group names.
    private static SmsStatus MapStatus(string raw) => raw.ToUpperInvariant() switch
    {
        "DELIVERED" or "DELIVERED_TO_HANDSET" => SmsStatus.Delivered,
        "ACCEPTED" or "PROCESSING" or "PENDING" or "PENDING_ENROUTE" => SmsStatus.Sent,
        "EXPIRED" => SmsStatus.Expired,
        "REJECTED" => SmsStatus.Rejected,
        "UNDELIVERED" or "UNDELIVERABLE" or "FAILED" => SmsStatus.Failed,
        _ => SmsStatus.Sent
    };
}

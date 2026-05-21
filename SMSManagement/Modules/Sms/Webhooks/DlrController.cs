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
/// Delivery-Receipt (DLR/DN) ingress. Providers call here when the carrier
/// confirms (or rejects) a previously-dispatched message. Updates
/// SmsMessage.{Status, DeliveredAt, ErrorCode}.
///
/// Wire format differs per provider (etracker = query/form params, Infobip =
/// JSON body); both are authenticated by a shared-secret token in the URL,
/// then translated into the common SmsStatus enum.
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

    /// <summary>
    /// Infobip delivery report. Infobip POSTs an unsigned JSON body
    /// (<c>{ "results": [ { "messageId", "status": { "groupName" } } ] }</c>)
    /// to the configured notify URL — authenticated, like the etracker DN, by
    /// a shared-secret token in the URL: …/api/sms/dlr/infobip?token=THE_SECRET
    /// </summary>
    [HttpPost("infobip")]
    public async Task<IActionResult> Infobip(CancellationToken ct)
    {
        if (!TokenValid(_secrets.InfobipDnToken))
        {
            _log.LogWarning("Infobip DN rejected — missing or wrong token.");
            return Unauthorized();
        }

        using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
            return BadRequest("results array required.");

        foreach (var r in results.EnumerateArray())
        {
            var id = r.TryGetProperty("messageId", out var mid) ? mid.GetString() : null;
            var group = r.TryGetProperty("status", out var st)
                        && st.TryGetProperty("groupName", out var gn) ? gn.GetString() : null;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(group)) continue;
            _log.LogInformation("Infobip DN messageId={MsgId} group={Group}", id, group);
            await ApplyAsync("infobip", id, MapStatus(group), null, ct);
        }
        return Ok();
    }

    // ---- helpers ----

    /// <summary>Constant-time comparison of the URL <c>token</c> param against
    /// the configured DN secret for the provider.</summary>
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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Observability;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Sms.Services;

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
    private readonly IOptionsMonitor<DlrWebhookOptions> _secrets;
    private readonly TimeProvider _clock;
    private readonly CampaignMetrics _metrics;
    private readonly ILogger<DlrController> _log;

    public DlrController(
        AppDbContext db,
        IOptionsMonitor<DlrWebhookOptions> secrets,
        TimeProvider clock,
        CampaignMetrics metrics,
        ILogger<DlrController> log)
    {
        _db = db;
        _secrets = secrets;
        _clock = clock;
        _metrics = metrics;
        _log = log;
    }

    /// <summary>
    /// etracker (MACROKIOSK) delivery notification. Unlike the HMAC-signed
    /// Infobip webhook, etracker DN is an unsigned GET/POST carrying form or
    /// query params (msgID, msisdn, status, statusDetail). Auth accepts any of
    /// the four schemes documented on <see cref="DlrWebhookOptions"/>: query
    /// param, path segment, header, or IP allowlist.
    /// </summary>
    [HttpGet("etracker")]
    [HttpPost("etracker")]
    [HttpGet("etracker/{token}")]
    [HttpPost("etracker/{token}")]
    public async Task<IActionResult> Etracker(string? token, CancellationToken ct)
    {
        var opts = _secrets.CurrentValue;
        if (!IsAuthorised(opts.EtrackerDnToken, opts.EtrackerDnAllowedIps,
                          opts.EtrackerDnAllowAnonymous, token))
        {
            _log.LogWarning("etracker DN rejected — missing/wrong token and IP not allowlisted.");
            return Unauthorized();
        }

        var msgId = Param("msgID");
        var status = Param("status");
        var detail = Param("statusDetail");

        if (string.IsNullOrEmpty(msgId) || string.IsNullOrEmpty(status))
            return BadRequest("msgID and status are required.");

        var mapped = DlrStatusMap.Map(status);
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
    /// to the configured notify URL — same auth options as etracker (query
    /// param, path segment, header, or IP allowlist).
    /// </summary>
    [HttpPost("infobip")]
    [HttpPost("infobip/{token}")]
    public async Task<IActionResult> Infobip(string? token, CancellationToken ct)
    {
        var opts = _secrets.CurrentValue;
        if (!IsAuthorised(opts.InfobipDnToken, opts.InfobipDnAllowedIps,
                          opts.InfobipDnAllowAnonymous, token))
        {
            _log.LogWarning("Infobip DN rejected — missing/wrong token and IP not allowlisted.");
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
            await ApplyAsync("infobip", id, DlrStatusMap.Map(group), null, ct);
        }
        return Ok();
    }

    // ---- helpers ----

    /// <summary>
    /// Accepts the request if ANY of the following holds (cheapest first):
    /// <list type="number">
    ///   <item>anonymous mode is enabled for the provider,</item>
    ///   <item>a valid token is supplied in the route, query/form, or
    ///         X-DN-Token header,</item>
    ///   <item>the remote IP matches the provider's allowlist.</item>
    /// </list>
    /// Token comparison is constant-time.
    /// </summary>
    private bool IsAuthorised(
        string? configuredToken, string[] allowedIps, bool allowAnonymous,
        string? routeToken)
    {
        if (allowAnonymous) return true;
        if (TokenMatches(configuredToken, routeToken)) return true;
        if (TokenMatches(configuredToken, Param("token"))) return true;
        if (Request.Headers.TryGetValue("X-DN-Token", out var hv)
            && TokenMatches(configuredToken, hv.ToString())) return true;

        if (allowedIps.Length > 0)
        {
            var remote = HttpContext.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(remote)
                && allowedIps.Any(ip => string.Equals(ip, remote, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    private static bool TokenMatches(string? configured, string? supplied)
    {
        if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrEmpty(supplied)) return false;
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
        // IgnoreQueryFilters: the DLR webhook is an anonymous (token-verified)
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

}

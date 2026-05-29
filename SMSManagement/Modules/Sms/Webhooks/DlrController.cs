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
            await WriteDnLogAsync("etracker", "webhook", null, "unauthorized",
                null, null, null, null, null,
                Notes: "missing/wrong token and IP not in allowlist", ct);
            return Unauthorized();
        }

        var msgId = Param("msgID");
        var status = Param("status");
        var detail = Param("statusDetail");

        var rawForBadRequest = CaptureAllParams();
        if (string.IsNullOrEmpty(msgId) || string.IsNullOrEmpty(status))
        {
            await WriteDnLogAsync("etracker", "webhook", msgId, "parse-error",
                status, null, null,
                SerialiseRaw(rawForBadRequest), string.Join(",", rawForBadRequest.Keys),
                Notes: "msgID or status missing", ct);
            return BadRequest("msgID and status are required.");
        }

        var mapped = DlrStatusMap.Map(status);
        // Carry the failure reason into ErrorCode for non-delivered receipts.
        var code = mapped is SmsStatus.Failed or SmsStatus.Rejected or SmsStatus.Expired
            ? $"DN_{status.ToUpperInvariant()}"
              + (string.IsNullOrWhiteSpace(detail) ? "" : $": {detail}")
            : null;
        // StatusDetail is informational for ALL statuses (incl. DELIVERED) so
        // the operator sees the raw provider text — "DELIVERED: 11:05:23" etc.
        var statusDetail = string.IsNullOrWhiteSpace(detail)
            ? status.ToUpperInvariant()
            : $"{status.ToUpperInvariant()}: {detail}";

        // Capture every param etracker sent (query + form) verbatim. Different
        // MacroKiosk accounts include different extras — carrier timestamp,
        // operator id, charge units — and we want the operator to see them
        // even before we add typed columns for each.
        var raw = rawForBadRequest;
        // Best-effort: pull a carrier-side timestamp out of the captured
        // payload. MacroKiosk variants use one of these field names.
        var carrierAt = ExtractCarrierTimestamp(raw,
            "Received", "Done", "Sent", "DLR_TIMESTAMP",
            "deliveredAt", "deliveryTime", "doneAt");

        // Log the FULL field list at info level — operators tracking down a
        // missing CarrierDeliveredAt can look here to see exactly which keys
        // etracker sent (and whether any of them carry a timestamp we should
        // be parsing). Sensitive token already stripped by CaptureAllParams.
        _log.LogInformation(
            "etracker DN msgID={MsgId} status={Status} -> {Mapped} carrierAt={CarrierAt} fields=[{Fields}]",
            msgId, status, mapped, carrierAt, string.Join(",", raw.Keys));

        var rawJson = SerialiseRaw(raw);
        var outcome = await ApplyAsync("etracker", msgId, mapped, code, statusDetail,
            carrierAt, rawJson, ct);

        await WriteDnLogAsync("etracker", "webhook", msgId, outcome,
            status, mapped.ToString(), carrierAt,
            rawJson, string.Join(",", raw.Keys), Notes: null, ct);
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
            await WriteDnLogAsync("infobip", "webhook", null, "unauthorized",
                null, null, null, null, null,
                Notes: "missing/wrong token and IP not in allowlist", ct);
            return Unauthorized();
        }

        using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            await WriteDnLogAsync("infobip", "webhook", null, "parse-error",
                null, null, null, null, null,
                Notes: "missing results[] in body", ct);
            return BadRequest("results array required.");
        }

        foreach (var r in results.EnumerateArray())
        {
            var id = r.TryGetProperty("messageId", out var mid) ? mid.GetString() : null;
            var group = r.TryGetProperty("status", out var st)
                        && st.TryGetProperty("groupName", out var gn) ? gn.GetString() : null;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(group))
            {
                await WriteDnLogAsync("infobip", "webhook", id, "parse-error",
                    null, null, null, r.GetRawText(), null,
                    Notes: "messageId or status.groupName missing", ct);
                continue;
            }

            // Infobip carries the carrier-side timestamp as ISO-8601 in
            // doneAt; sentAt is a fallback for pre-delivery events.
            DateTimeOffset? carrierAt = null;
            if (r.TryGetProperty("doneAt", out var dn) && dn.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(dn.GetString(), out var parsedDone))
                carrierAt = parsedDone;
            else if (r.TryGetProperty("sentAt", out var sn) && sn.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(sn.GetString(), out var parsedSent))
                carrierAt = parsedSent;

            _log.LogInformation("Infobip DN messageId={MsgId} group={Group} carrierAt={CarrierAt}",
                id, group, carrierAt);
            var mapped = DlrStatusMap.Map(group);
            var rawJson = r.GetRawText();
            var outcome = await ApplyAsync("infobip", id, mapped, null,
                statusDetail: group.ToUpperInvariant(),
                carrierDeliveredAt: carrierAt,
                rawPayload: rawJson, ct);

            // Top-level field names from this result object — useful for
            // diagnosing missing carrierAt across Infobip API versions.
            var keys = new List<string>();
            foreach (var p in r.EnumerateObject()) keys.Add(p.Name);
            await WriteDnLogAsync("infobip", "webhook", id, outcome,
                group, mapped.ToString(), carrierAt,
                rawJson, string.Join(",", keys), Notes: null, ct);
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

    /// <summary>Snapshots every query and form parameter the provider sent.
    /// Reserved auth/token fields are excluded so the captured payload never
    /// records the shared secret.</summary>
    private Dictionary<string, string> CaptureAllParams()
    {
        var bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Request.Query)
        {
            if (string.Equals(kv.Key, "token", StringComparison.OrdinalIgnoreCase)) continue;
            bag[kv.Key] = kv.Value.ToString();
        }
        if (Request.HasFormContentType)
            foreach (var kv in Request.Form)
            {
                if (string.Equals(kv.Key, "token", StringComparison.OrdinalIgnoreCase)) continue;
                bag[kv.Key] = kv.Value.ToString();
            }
        return bag;
    }

    /// <summary>Try the given field names in order; the first one that parses
    /// as a date wins. MacroKiosk variants use different names (Received /
    /// Done / DLR_TIMESTAMP / …) — capture them all so the operator can see
    /// "ลูกค้าได้รับจริง" instead of just "เราได้รับ DN ตอนกี่โมง".
    /// As a final fallback, scan EVERY field for anything that parses as a
    /// datetime — handles accounts that use a field name we haven't seen
    /// before. Obvious non-timestamp keys (msgID, status, msisdn, …) are
    /// excluded so a numeric msgID isn't mistaken for a Unix epoch.</summary>
    private static DateTimeOffset? ExtractCarrierTimestamp(
        IReadOnlyDictionary<string, string> raw, params string[] candidateFields)
    {
        // First: try named fields (these always win — most accurate).
        foreach (var f in candidateFields)
            if (raw.TryGetValue(f, out var v)
                && !string.IsNullOrWhiteSpace(v)
                && DateTimeOffset.TryParse(v, out var parsed))
                return parsed;

        // Fallback: blind scan. Skip identifier / status fields and pure-digit
        // values (msgIDs, msisdns) so we don't pick up something that isn't a
        // timestamp. First parseable hit wins.
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "status", "statusDetail", "msgID", "msisdn", "description",
            "errorCode", "operatorID", "MsgID", "Status", "Description",
            "from", "to", "sender", "recipient"
        };
        foreach (var kv in raw)
        {
            if (skip.Contains(kv.Key)) continue;
            var v = kv.Value;
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (v.All(char.IsDigit)) continue; // pure number = id, not date
            if (DateTimeOffset.TryParse(v, out var parsed)) return parsed;
        }
        return null;
    }

    private static string SerialiseRaw(Dictionary<string, string> raw)
    {
        var json = JsonSerializer.Serialize(raw);
        // Cap at 4 KB — keeps the column small without losing realistic
        // payloads. Truncation is signalled with a sentinel so an operator
        // viewing the field knows to consult logs for the rest.
        return json.Length > 4096 ? json[..4096] + "\"…(truncated)\"" : json;
    }

    /// <summary>Persists the DN-aware fields onto the matching SmsMessage and
    /// returns a coarse outcome label — <c>accepted</c>, <c>ignored-stale</c>
    /// (status already terminal-Delivered), or <c>unknown-message</c> when
    /// no SmsMessage matches the provider/id pair. The label feeds the
    /// per-DN audit row (<see cref="DnLog"/>).</summary>
    private async Task<string> ApplyAsync(
        string provider, string providerMessageId, SmsStatus newStatus,
        string? errorCode, string? statusDetail,
        DateTimeOffset? carrierDeliveredAt, string? rawPayload,
        CancellationToken ct)
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
            return "unknown-message";
        }

        // Stamp the "we received a DN" trail on every webhook hit, even for
        // late/duplicate receipts — gives the report a non-null DnReceivedAt
        // for Failed/Rejected rows that aren't otherwise timestamped.
        var now = _clock.GetUtcNow();
        msg.DnReceivedAt = now;
        if (statusDetail is not null) msg.StatusDetail = statusDetail;
        msg.StatusSource = "webhook";
        if (carrierDeliveredAt is not null) msg.CarrierDeliveredAt = carrierDeliveredAt;
        if (rawPayload is not null) msg.DnRawPayload = rawPayload;

        // Delivered is terminal — a late ACCEPTED/PROCESSING receipt (DNs can
        // arrive out of order) must not downgrade it.
        if (msg.Status == SmsStatus.Delivered && newStatus != SmsStatus.Delivered)
        {
            _log.LogInformation("DLR {Status} for already-delivered {Provider}/{Id} — ignored.",
                newStatus, provider, providerMessageId);
            await _db.SaveChangesAsync(ct);
            return "ignored-stale";
        }

        msg.Status = newStatus;
        if (newStatus == SmsStatus.Delivered)
        {
            // Prefer the carrier-side timestamp when the provider sent one —
            // it's the actual handset-arrival time, which is what the
            // operator (and customer) cares about. Fall back to wall-clock
            // now only when the provider didn't include a timestamp.
            msg.DeliveredAt = carrierDeliveredAt ?? now;
            _metrics.SmsDelivered.Add(1, KeyValuePair.Create<string, object?>("provider", provider));
        }
        else if (newStatus is SmsStatus.Failed or SmsStatus.Rejected)
        {
            _metrics.SmsFailed.Add(1, KeyValuePair.Create<string, object?>("provider", provider));
        }
        if (errorCode is not null) msg.ErrorCode = errorCode;
        await _db.SaveChangesAsync(ct);
        return "accepted";
    }

    /// <summary>Inserts a structured per-DN audit row into <c>DnLogs</c> —
    /// captured for every webhook hit (auth-rejected, parse-failed,
    /// unknown-message, or accepted). Best-effort: a failure here logs to
    /// Serilog but never throws so it can't break the webhook handler.</summary>
    private async Task WriteDnLogAsync(
        string provider, string source, string? providerMessageId, string outcome,
        string? status, string? mappedStatus, DateTimeOffset? carrierAt,
        string? rawPayload, string? fieldKeys, string? Notes, CancellationToken ct)
    {
        try
        {
            _db.DnLogs.Add(new DnLog
            {
                CreatedAt = _clock.GetUtcNow(),
                Provider = provider,
                Source = source,
                ProviderMessageId = providerMessageId,
                Outcome = outcome,
                Status = status,
                MappedStatus = mappedStatus,
                CarrierDeliveredAt = carrierAt,
                RawPayload = rawPayload,
                FieldKeys = fieldKeys,
                RemoteIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Notes = Notes
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "DnLog write failed for {Provider} msgId={MsgId} outcome={Outcome}",
                provider, providerMessageId, outcome);
        }
    }
}

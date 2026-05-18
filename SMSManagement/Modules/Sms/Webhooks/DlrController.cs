using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
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
    private readonly ILogger<DlrController> _log;

    public DlrController(
        AppDbContext db,
        IOptions<DlrWebhookOptions> secrets,
        TimeProvider clock,
        ILogger<DlrController> log)
    {
        _db = db;
        _secrets = secrets.Value;
        _clock = clock;
        _log = log;
    }

    [HttpPost("etracker")]
    public async Task<IActionResult> Etracker(CancellationToken ct)
    {
        if (!await VerifyAsync(_secrets.EtrackerSecretBase64, ct)) return Unauthorized();
        Request.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);

        // Etracker DLR shape (representative): { "messageId": "...", "status": "DELIVERED"|"FAILED", "code": "..." }
        var id = doc.RootElement.GetProperty("messageId").GetString();
        var status = doc.RootElement.GetProperty("status").GetString();
        var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(status))
            return BadRequest("messageId and status required.");

        await ApplyAsync("etracker", id, MapStatus(status), code, ct);
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
        var msg = await _db.SmsMessages
            .FirstOrDefaultAsync(m => m.Provider == provider
                                   && m.ProviderMessageId == providerMessageId, ct);
        if (msg is null)
        {
            _log.LogInformation("DLR for unknown {Provider}/{Id} — ignored.",
                provider, providerMessageId);
            return;
        }

        msg.Status = newStatus;
        if (newStatus == SmsStatus.Delivered) msg.DeliveredAt = _clock.GetUtcNow();
        if (errorCode is not null) msg.ErrorCode = errorCode;
        await _db.SaveChangesAsync(ct);
    }

    private static SmsStatus MapStatus(string raw) => raw.ToUpperInvariant() switch
    {
        "DELIVERED" or "DELIVERED_TO_HANDSET" => SmsStatus.Delivered,
        "PENDING" or "PENDING_ENROUTE" => SmsStatus.Sent,
        "EXPIRED" => SmsStatus.Expired,
        "REJECTED" => SmsStatus.Rejected,
        "UNDELIVERABLE" or "FAILED" => SmsStatus.Failed,
        _ => SmsStatus.Sent
    };
}

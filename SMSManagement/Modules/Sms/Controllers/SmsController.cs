using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Sms.Services;

namespace SMSManagement.Modules.Sms.Controllers;

[ApiController]
[Authorize(Policy = "sms.dispatch")]
[Route("api/projects/{projectId:guid}/sms")]
public sealed class SmsController : ControllerBase
{
    private readonly ISmsDispatcher _dispatcher;
    private readonly AppDbContext _db;
    private readonly IProjectAccessService _access;
    private readonly IProjectFeatureGuard _features;
    private readonly FieldEncryptor _crypto;

    public SmsController(ISmsDispatcher dispatcher, AppDbContext db,
        IProjectAccessService access, IProjectFeatureGuard features, FieldEncryptor crypto)
    {
        _dispatcher = dispatcher;
        _db = db;
        _access = access;
        _features = features;
        _crypto = crypto;
    }

    public sealed record SendRequest(
        string Recipient,
        string Body,
        string? SenderId,
        DateTimeOffset? ScheduledFor);

    /// <summary>
    /// Ad-hoc / "send-now" endpoint. Workflow-driven dispatches go through
    /// the Workflow Engine; this is for one-off operator messages and tests.
    /// </summary>
    [HttpPost("send")]
    public async Task<IActionResult> Send(
        Guid projectId, [FromBody] SendRequest req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Member, ct);
        await _features.EnsureAsync(projectId, ProjectFeature.Sms, ct);

        if (string.IsNullOrWhiteSpace(req.Recipient) || string.IsNullOrWhiteSpace(req.Body))
            return BadRequest("Recipient and Body are required.");

        var priority = req.ScheduledFor is { } ? SmsPriority.Scheduled : SmsPriority.Immediate;
        // Ad-hoc operator sends are deliberately given a unique dedup
        // discriminator: the dedup key exists to absorb automated double-runs,
        // not to permanently block an operator from re-sending the same body
        // to the same number (e.g. test messages). Accidental double-clicks
        // are guarded client-side instead.
        var id = await _dispatcher.EnqueueAsync(new SmsRequest(
            projectId, req.Recipient, req.Body, req.SenderId,
            priority, req.ScheduledFor, WorkflowInstanceId: null,
            DedupDiscriminator: $"adhoc:{Guid.NewGuid():N}"), ct);

        // Synchronously dispatch unscheduled messages so the operator sees provider
        // feedback inline. Scheduled messages are picked up by the worker.
        if (priority == SmsPriority.Immediate)
            await _dispatcher.DispatchAsync(id, ct);

        var msg = await _db.SmsMessages
            .AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new
            {
                m.Id, m.Provider, m.Status, m.MaskedTo,
                m.SentAt, m.ProviderMessageId, m.ErrorCode
            })
            .FirstAsync(ct);
        return Ok(msg);
    }

    /// <summary>
    /// Re-dispatch a previously failed / rejected SMS. Resets attempt counter
    /// and ErrorCode, flips status back to Queued, then drives the dispatcher
    /// synchronously so the operator sees the new outcome inline.
    /// Only Failed / Rejected / Expired messages are eligible — Sent /
    /// Delivered messages would be a duplicate send.
    /// </summary>
    [HttpPost("{messageId:guid}/retry")]
    public async Task<IActionResult> Retry(Guid projectId, Guid messageId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Member, ct);
        await _features.EnsureAsync(projectId, ProjectFeature.Sms, ct);

        var msg = await _db.SmsMessages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ProjectId == projectId, ct);
        if (msg is null) return NotFound();

        if (msg.Status is SmsStatus.Sent or SmsStatus.Delivered)
            return Conflict(new { Message = $"Message is {msg.Status}; cannot retry — duplicate send." });

        msg.Status = SmsStatus.Queued;
        msg.Attempts = 0;
        msg.ErrorCode = null;
        msg.SentAt = null;
        msg.DeliveredAt = null;
        await _db.SaveChangesAsync(ct);

        await _dispatcher.DispatchAsync(messageId, ct);

        var refreshed = await _db.SmsMessages
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.Id, m.Status, m.ProviderMessageId, m.ErrorCode, m.Attempts, m.SentAt })
            .FirstAsync(ct);
        return Ok(refreshed);
    }

    [HttpGet("{messageId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid messageId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var m = await _db.SmsMessages
            .AsNoTracking()
            .Where(x => x.Id == messageId && x.ProjectId == projectId)
            .Select(x => new
            {
                x.Id, x.Provider, x.SenderId, x.Status, x.MaskedTo, x.Attempts,
                x.CreatedAt, x.ScheduledFor, x.SentAt, x.DeliveredAt,
                x.ProviderMessageId, x.ErrorCode, x.RawProviderResponse, x.EncryptedBody
            })
            .FirstOrDefaultAsync(ct);
        if (m is null) return NotFound();

        // Decrypt the body so the operator can see the exact message that was
        // sent — incl. whether a URL became a shortlink.
        string body;
        try { body = _crypto.Decrypt(m.EncryptedBody); }
        catch { body = "(decrypt failed)"; }

        return Ok(new
        {
            m.Id, m.Provider, m.SenderId, m.Status, m.MaskedTo, m.Attempts,
            m.CreatedAt, m.ScheduledFor, m.SentAt, m.DeliveredAt,
            m.ProviderMessageId, m.ErrorCode, m.RawProviderResponse,
            Body = body
        });
    }

    private IQueryable<SmsMessage> FilteredHistory(Guid projectId, string? status)
    {
        var q = _db.SmsMessages.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(status)
            && Enum.TryParse<SmsStatus>(status, ignoreCase: true, out var s))
            q = q.Where(x => x.Status == s);
        return q;
    }

    /// <summary>SMS dispatched for this project — the SMS tab history list,
    /// latest first, server-side paged.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 10, 200);

        var q = FilteredHistory(projectId, status);
        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id, x.Provider, x.Status, x.MaskedTo, x.Attempts,
                x.CreatedAt, x.ScheduledFor, x.SentAt, x.DeliveredAt,
                x.ErrorCode
            })
            .ToListAsync(ct);

        return Ok(new
        {
            page,
            pageSize,
            total,
            totalPages = total == 0 ? 0 : (total + pageSize - 1) / pageSize,
            items = rows
        });
    }

    /// <summary>Exports the (optionally status-filtered) SMS history as CSV —
    /// the message body is decrypted so the export is a complete per-message
    /// log. Capped at 10,000 rows.</summary>
    [HttpGet("export.csv")]
    public async Task<IActionResult> Export(
        Guid projectId,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        var rows = await FilteredHistory(projectId, status)
            .OrderByDescending(x => x.CreatedAt)
            .Take(10000)
            .Select(x => new
            {
                x.CreatedAt, x.MaskedTo, x.Provider, x.SenderId, x.Status,
                x.Attempts, x.SentAt, x.DeliveredAt, x.ErrorCode,
                x.ProviderMessageId, x.RawProviderResponse, x.EncryptedBody
            })
            .ToListAsync(ct);

        var sb = new System.Text.StringBuilder();
        sb.Append('﻿');   // UTF-8 BOM so Excel opens it cleanly
        sb.AppendLine("CreatedAt,Recipient,Provider,Sender,Status,Attempts,"
                    + "SentAt,DeliveredAt,ErrorCode,ProviderMessageId,ProviderResponse,Body");
        foreach (var r in rows)
        {
            string body;
            try { body = _crypto.Decrypt(r.EncryptedBody); }
            catch { body = "(decrypt failed)"; }
            sb.AppendLine(string.Join(',', new[]
            {
                Csv(r.CreatedAt.ToString("u")),
                Csv(r.MaskedTo),
                Csv(r.Provider),
                Csv(r.SenderId),
                Csv(r.Status.ToString()),
                Csv(r.Attempts.ToString()),
                Csv(r.SentAt?.ToString("u")),
                Csv(r.DeliveredAt?.ToString("u")),
                Csv(r.ErrorCode),
                Csv(r.ProviderMessageId),
                Csv(r.RawProviderResponse),
                Csv(body)
            }));
        }
        return File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()),
            "text/csv", $"sms-history-{projectId:N}.csv");
    }

    private static string Csv(string? v)
    {
        v ??= string.Empty;
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Time;

namespace SMSManagement.Modules.Sms.Webhooks;

/// <summary>
/// Read-only viewer for the DnLogs table — a structured trail of every DN
/// (webhook push or pull-status) the system has received, captured for
/// diagnosing missing CarrierDeliveredAt, unauthorised hits from the
/// provider's IP, and "did the DN ever arrive?" questions without scraping
/// JSON log files. Gated by <c>system_admin</c>.
/// </summary>
[ApiController]
[Authorize(Policy = "system_admin")]
[Route("api/admin/dn-logs")]
public sealed class DnLogsController : ControllerBase
{
    private readonly AppDbContext _db;
    public DnLogsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] string? provider = null,
        [FromQuery] string? source = null,
        [FromQuery] string? outcome = null,
        [FromQuery] string? msgId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 10, 200);

        var (total, rows) = await FetchAsync(
            from, to, provider, source, outcome, msgId, (page - 1) * pageSize, pageSize, ct);

        return Ok(new
        {
            page,
            pageSize,
            total,
            totalPages = total == 0 ? 0 : (total + pageSize - 1) / pageSize,
            items = rows.Select(e => new
            {
                e.Id, e.CreatedAt, e.Provider, e.Source, e.ProviderMessageId,
                e.Outcome, e.Status, e.MappedStatus,
                e.FieldKeys, e.RemoteIp,
                e.RawPayload, e.Notes
            })
        });
    }

    [HttpGet("export.csv")]
    public async Task<IActionResult> Export(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] string? provider = null,
        [FromQuery] string? source = null,
        [FromQuery] string? outcome = null,
        [FromQuery] string? msgId = null,
        CancellationToken ct = default)
    {
        var (_, rows) = await FetchAsync(from, to, provider, source, outcome, msgId, 0, 10000, ct);

        var sb = new System.Text.StringBuilder();
        sb.Append('﻿');
        sb.AppendLine("CreatedAt,Provider,Source,ProviderMessageId,Outcome,Status,MappedStatus,"
                    + "RemoteIp,FieldKeys,Notes,RawPayload");
        foreach (var e in rows)
            sb.AppendLine(string.Join(',', new[]
            {
                Csv(LocalTime.Format(e.CreatedAt)), Csv(e.Provider), Csv(e.Source),
                Csv(e.ProviderMessageId), Csv(e.Outcome), Csv(e.Status), Csv(e.MappedStatus),
                Csv(e.RemoteIp), Csv(e.FieldKeys), Csv(e.Notes), Csv(e.RawPayload)
            }));
        return File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()),
            "text/csv", "dn-logs.csv");
    }

    // Mirrors ErrorLogsController.FetchAsync — SQL Server path uses indexed
    // server-side filtering; SQLite test path falls back to a bounded
    // in-memory scan because composite DateTimeOffset / nullable predicates
    // don't translate cleanly through the SQLite provider.
    private async Task<(int total, List<DnLog> rows)> FetchAsync(
        DateTimeOffset from, DateTimeOffset to,
        string? provider, string? source, string? outcome, string? msgId,
        int skip, int take, CancellationToken ct)
    {
        if (_db.Database.IsSqlServer())
        {
            var q = _db.DnLogs
                .AsNoTracking()
                .Where(e => e.CreatedAt >= from && e.CreatedAt < to);

            if (!string.IsNullOrWhiteSpace(provider)) q = q.Where(e => e.Provider == provider);
            if (!string.IsNullOrWhiteSpace(source))   q = q.Where(e => e.Source == source);
            if (!string.IsNullOrWhiteSpace(outcome))  q = q.Where(e => e.Outcome == outcome);
            if (!string.IsNullOrWhiteSpace(msgId))    q = q.Where(e => e.ProviderMessageId == msgId);

            var total = await q.CountAsync(ct);
            var rows = await q
                .OrderByDescending(e => e.CreatedAt)
                .Skip(skip).Take(take)
                .ToListAsync(ct);
            return (total, rows);
        }

        var raw = await _db.DnLogs.AsNoTracking().Take(20000).ToListAsync(ct);
        var filtered = raw
            .Where(e => e.CreatedAt >= from && e.CreatedAt < to)
            .Where(e => string.IsNullOrEmpty(provider) || e.Provider == provider)
            .Where(e => string.IsNullOrEmpty(source) || e.Source == source)
            .Where(e => string.IsNullOrEmpty(outcome) || e.Outcome == outcome)
            .Where(e => string.IsNullOrEmpty(msgId) || e.ProviderMessageId == msgId)
            .OrderByDescending(e => e.CreatedAt)
            .ToList();
        return (filtered.Count, filtered.Skip(skip).Take(take).ToList());
    }

    private static string Csv(string? v)
    {
        v ??= string.Empty;
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }
}

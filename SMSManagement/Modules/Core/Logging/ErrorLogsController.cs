using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Time;

namespace SMSManagement.Modules.Core.Logging;

/// <summary>
/// Read-only viewer for the ErrorLogs table. Gated by <c>audit.read</c>
/// (system admins / SecOps). No write endpoints — rows are inserted by the
/// Serilog sink and aged out by the Hangfire <c>error-log-purge</c> job.
/// </summary>
[ApiController]
[Authorize(Policy = "system_admin")]
[Route("api/admin/error-logs")]
public sealed class ErrorLogsController : ControllerBase
{
    private readonly AppDbContext _db;
    public ErrorLogsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] string? level = null,
        [FromQuery] string? module = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 10, 200);

        var (total, rows) = await FetchAsync(
            from, to, level, module, search, (page - 1) * pageSize, pageSize, ct);

        return Ok(new
        {
            page,
            pageSize,
            total,
            totalPages = total == 0 ? 0 : (total + pageSize - 1) / pageSize,
            items = rows.Select(e => new
            {
                e.Id, e.CreatedAt, e.Level, e.SourceContext, e.Message,
                e.ExceptionType, e.ExceptionMessage, e.ExceptionStackTrace,
                e.RequestPath, e.RequestMethod, e.CorrelationId, e.IpAddress, e.UserId
            })
        });
    }

    /// <summary>Exports the filtered log as CSV (capped at 10,000 rows).</summary>
    [HttpGet("export.csv")]
    public async Task<IActionResult> Export(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] string? level = null,
        [FromQuery] string? module = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var (_, rows) = await FetchAsync(from, to, level, module, search, 0, 10000, ct);

        var sb = new System.Text.StringBuilder();
        sb.Append('﻿');   // UTF-8 BOM
        sb.AppendLine("CreatedAt,Level,Source,Message,ExceptionType,ExceptionMessage,"
                    + "RequestMethod,RequestPath,CorrelationId,UserId");
        foreach (var e in rows)
            sb.AppendLine(string.Join(',', new[]
            {
                Csv(LocalTime.Format(e.CreatedAt)), Csv(e.Level), Csv(e.SourceContext),
                Csv(e.Message), Csv(e.ExceptionType), Csv(e.ExceptionMessage),
                Csv(e.RequestMethod), Csv(e.RequestPath), Csv(e.CorrelationId),
                Csv(e.UserId?.ToString())
            }));
        return File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()),
            "text/csv", "error-logs.csv");
    }

    // SQL-Server-first path; SQLite test path uses a bounded fetch + in-memory
    // filter (combined DateTimeOffset / nullable predicates don't translate
    // cleanly). Returns the total matching count and the requested slice.
    private async Task<(int total, List<ErrorLog> rows)> FetchAsync(
        DateTimeOffset from, DateTimeOffset to, string? level, string? module,
        string? search, int skip, int take, CancellationToken ct)
    {
        if (_db.Database.IsSqlServer())
        {
            var q = _db.ErrorLogs
                .AsNoTracking()
                .Where(e => e.CreatedAt >= from && e.CreatedAt < to);

            if (!string.IsNullOrWhiteSpace(level))
                q = q.Where(e => e.Level == level);

            if (!string.IsNullOrWhiteSpace(module))
                q = q.Where(e => EF.Functions.Like(e.SourceContext ?? string.Empty, $"%{module}%"));

            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = $"%{search}%";
                q = q.Where(e =>
                    EF.Functions.Like(e.Message, needle)
                    || EF.Functions.Like(e.ExceptionMessage ?? string.Empty, needle)
                    || EF.Functions.Like(e.ExceptionType    ?? string.Empty, needle)
                    || EF.Functions.Like(e.RequestPath      ?? string.Empty, needle));
            }

            var total = await q.CountAsync(ct);
            var rows = await q
                .OrderByDescending(e => e.CreatedAt)
                .Skip(skip).Take(take)
                .ToListAsync(ct);
            return (total, rows);
        }

        var raw = await _db.ErrorLogs.AsNoTracking().Take(20000).ToListAsync(ct);
        var filtered = raw
            .Where(e => e.CreatedAt >= from && e.CreatedAt < to)
            .Where(e => string.IsNullOrEmpty(level) || e.Level == level)
            .Where(e => string.IsNullOrEmpty(module) ||
                (e.SourceContext?.Contains(module, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(e => string.IsNullOrEmpty(search) ||
                (e.Message?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.ExceptionMessage?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.ExceptionType?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.RequestPath?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
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

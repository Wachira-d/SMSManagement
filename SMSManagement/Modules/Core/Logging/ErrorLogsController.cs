using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Core.Logging;

/// <summary>
/// Read-only viewer for the ErrorLogs table. Gated by <c>audit.read</c>
/// (system admins / SecOps). No write endpoints — rows are inserted by the
/// Serilog sink and aged out by the Hangfire <c>error-log-purge</c> job.
/// </summary>
[ApiController]
[Authorize(Policy = "audit.read")]
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
        [FromQuery] int take = 200,
        CancellationToken ct = default)
    {
        var cap = Math.Clamp(take, 1, 1000);

        // SQL-Server-first path; SQLite test path uses a bounded fetch + in-
        // memory filter for the same reason as AuditTrailAsync (combined
        // DateTimeOffset / nullable predicates don't translate cleanly).
        List<ErrorLog> rows;
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

            rows = await q.OrderByDescending(e => e.CreatedAt).Take(cap).ToListAsync(ct);
        }
        else
        {
            var raw = await _db.ErrorLogs.AsNoTracking()
                .Take(cap * 8).ToListAsync(ct);
            rows = raw
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
                .Take(cap)
                .ToList();
        }

        return Ok(rows.Select(e => new
        {
            e.Id, e.CreatedAt, e.Level, e.SourceContext, e.Message,
            e.ExceptionType, e.ExceptionMessage, e.ExceptionStackTrace,
            e.RequestPath, e.RequestMethod, e.CorrelationId, e.IpAddress, e.UserId
        }));
    }
}

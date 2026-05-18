using System.Globalization;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Reporting.Domain;
using SMSManagement.Modules.Shortlink.Domain;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Workflow.Domain;

namespace SMSManagement.Modules.Reporting.Services;

/// <summary>
/// All reports are scoped by the caller's accessible projects (enforced via
/// AppDbContext global query filter + explicit project guard).
///
/// Each method intentionally returns small, projection-only DTOs so the
/// response can stream straight to JSON/CSV without loading rich entities.
/// </summary>
public sealed class ReportingService : IReportingService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;

    public ReportingService(AppDbContext db, ICurrentUser me)
    {
        _db = db;
        _me = me;
    }

    public async Task<IReadOnlyList<SmsCampaignRow>> SmsCampaignAsync(
        Guid projectId, DateRange range, CancellationToken ct = default)
    {
        await EnsureProjectVisibleAsync(projectId, ct);

        // Group by date + provider; compute terminal-state aggregates. No raw PII selected.
        var raw = await _db.SmsMessages
            .Where(m => m.ProjectId == projectId
                     && m.CreatedAt >= range.From
                     && m.CreatedAt < range.To)
            .GroupBy(m => new { Day = m.CreatedAt.UtcDateTime.Date, m.Provider })
            .Select(g => new
            {
                g.Key.Day,
                g.Key.Provider,
                Queued    = g.Count(x => x.Status == SmsStatus.Queued),
                Sent      = g.Count(x => x.Status == SmsStatus.Sent),
                Delivered = g.Count(x => x.Status == SmsStatus.Delivered),
                Failed    = g.Count(x => x.Status == SmsStatus.Failed),
                Rejected  = g.Count(x => x.Status == SmsStatus.Rejected),
                Total     = g.Count()
            })
            .ToListAsync(ct);

        return raw.Select(r => new SmsCampaignRow(
            projectId, r.Provider, DateOnly.FromDateTime(r.Day),
            r.Queued, r.Sent, r.Delivered, r.Failed, r.Rejected,
            DeliveryRate: r.Total == 0 ? 0 : (double)r.Delivered / r.Total,
            FailureRate:  r.Total == 0 ? 0 : (double)(r.Failed + r.Rejected) / r.Total))
            .ToList();
    }

    public async Task<IReadOnlyList<ProviderPerformanceRow>> ProviderPerformanceAsync(
        DateRange range, CancellationToken ct = default)
    {
        var visible = await _me.AccessibleProjectIdsAsync(ct);

        // Aggregate counts and average attempts in SQL; compute latency client-side
        // to keep the expression provider-agnostic (DateDiff helpers vary across EF providers).
        var aggregates = await _db.SmsMessages
            .Where(m => visible.Contains(m.ProjectId)
                     && m.CreatedAt >= range.From && m.CreatedAt < range.To)
            .GroupBy(m => m.Provider)
            .Select(g => new
            {
                Provider = g.Key,
                Volume = g.Count(),
                Delivered = g.Count(x => x.Status == SmsStatus.Delivered),
                AvgAttempts = g.Average(x => (double)x.Attempts)
            })
            .ToListAsync(ct);

        // Second query: bounded set of (CreatedAt, SentAt) pairs for latency calc.
        var latencies = await _db.SmsMessages
            .Where(m => visible.Contains(m.ProjectId)
                     && m.CreatedAt >= range.From && m.CreatedAt < range.To
                     && m.SentAt != null)
            .Select(m => new { m.Provider, m.CreatedAt, SentAt = m.SentAt!.Value })
            .ToListAsync(ct);

        var avgLatency = latencies
            .GroupBy(x => x.Provider)
            .ToDictionary(g => g.Key,
                g => TimeSpan.FromMilliseconds(
                    g.Average(x => (x.SentAt - x.CreatedAt).TotalMilliseconds)));

        return aggregates.Select(r => new ProviderPerformanceRow(
            r.Provider, r.Volume,
            DeliveryRate: r.Volume == 0 ? 0 : (double)r.Delivered / r.Volume,
            AverageAttempts: r.AvgAttempts,
            CircuitTrips: 0, // populated by metrics sink in real impl
            AverageDispatchLatency: avgLatency.GetValueOrDefault(r.Provider, TimeSpan.Zero)))
            .ToList();
    }

    public async Task<DeliveryFunnelRow> DeliveryFunnelAsync(
        Guid projectId, DateRange range, CancellationToken ct = default)
    {
        await EnsureProjectVisibleAsync(projectId, ct);

        var targeted = await _db.WorkflowInstances
            .CountAsync(i => _db.WorkflowDefinitions
                .Any(d => d.Id == i.DefinitionId && d.ProjectId == projectId)
                && i.CreatedAt >= range.From && i.CreatedAt < range.To, ct);

        var sms = await _db.SmsMessages
            .Where(m => m.ProjectId == projectId
                     && m.CreatedAt >= range.From && m.CreatedAt < range.To)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Sent      = g.Count(x => x.SentAt != null),
                Delivered = g.Count(x => x.DeliveredAt != null)
            })
            .FirstOrDefaultAsync(ct) ?? new { Sent = 0, Delivered = 0 };

        var clicked = await _db.ShortlinkClicks
            .CountAsync(c => _db.Shortlinks
                .Any(s => s.Id == c.ShortlinkId && s.ProjectId == projectId)
                && c.ClickedAt >= range.From && c.ClickedAt < range.To, ct);

        var converted = await _db.WorkflowInstances
            .CountAsync(i => i.State == WorkflowState.Completed
                && _db.WorkflowDefinitions.Any(d => d.Id == i.DefinitionId && d.ProjectId == projectId)
                && i.CreatedAt >= range.From && i.CreatedAt < range.To, ct);

        return new DeliveryFunnelRow(
            projectId, targeted, sms.Sent, sms.Delivered, clicked, converted,
            SentRate:        targeted == 0 ? 0 : (double)sms.Sent / targeted,
            DeliveryRate:    sms.Sent == 0 ? 0 : (double)sms.Delivered / sms.Sent,
            ClickThroughRate: sms.Delivered == 0 ? 0 : (double)clicked / sms.Delivered,
            ConversionRate:   targeted == 0 ? 0 : (double)converted / targeted);
    }

    public async Task<IReadOnlyList<ShortlinkRow>> ShortlinkPerformanceAsync(
        Guid projectId, DateRange range, int top, CancellationToken ct = default)
    {
        await EnsureProjectVisibleAsync(projectId, ct);

        var raw = await _db.Shortlinks
            .Where(s => s.ProjectId == projectId)
            .Select(s => new
            {
                s.Id,
                s.Slug,
                Clicks = _db.ShortlinkClicks
                    .Count(c => c.ShortlinkId == s.Id
                             && c.ClickedAt >= range.From && c.ClickedAt < range.To),
                UniqueIps = _db.ShortlinkClicks
                    .Where(c => c.ShortlinkId == s.Id
                             && c.ClickedAt >= range.From && c.ClickedAt < range.To)
                    .Select(c => c.IpHash).Distinct().Count(),
                First = _db.ShortlinkClicks
                    .Where(c => c.ShortlinkId == s.Id)
                    .Min(c => (DateTimeOffset?)c.ClickedAt),
                Last = _db.ShortlinkClicks
                    .Where(c => c.ShortlinkId == s.Id)
                    .Max(c => (DateTimeOffset?)c.ClickedAt),
                TopDevice = _db.ShortlinkClicks
                    .Where(c => c.ShortlinkId == s.Id)
                    .GroupBy(c => c.DeviceClass ?? "unknown")
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key).FirstOrDefault() ?? "unknown",
                TopCountry = _db.ShortlinkClicks
                    .Where(c => c.ShortlinkId == s.Id)
                    .GroupBy(c => c.Country ?? "unknown")
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key).FirstOrDefault() ?? "unknown"
            })
            .OrderByDescending(r => r.Clicks)
            .Take(top)
            .ToListAsync(ct);

        return raw.Select(r => new ShortlinkRow(
            r.Id, r.Slug, r.Clicks, r.UniqueIps, r.First, r.Last, r.TopDevice, r.TopCountry))
            .ToList();
    }

    public async Task<IReadOnlyList<ReminderRow>> ReminderEffectivenessAsync(
        Guid projectId, CancellationToken ct = default)
    {
        await EnsureProjectVisibleAsync(projectId, ct);

        var defs = await _db.WorkflowDefinitions
            .Where(d => d.ProjectId == projectId)
            .Select(d => d.Id).ToListAsync(ct);

        var raw = await _db.WorkflowInstances
            .Where(i => defs.Contains(i.DefinitionId))
            .GroupBy(i => i.DefinitionId)
            .Select(g => new
            {
                DefinitionId = g.Key,
                R0 = g.Count(i => i.StepRepeatCount <= 1),
                R1 = g.Count(i => i.StepRepeatCount == 2),
                R2 = g.Count(i => i.StepRepeatCount == 3),
                R3 = g.Count(i => i.StepRepeatCount >= 4),
                C0 = g.Count(i => i.State == WorkflowState.Completed && i.StepRepeatCount <= 1),
                C1 = g.Count(i => i.State == WorkflowState.Completed && i.StepRepeatCount == 2),
                C2 = g.Count(i => i.State == WorkflowState.Completed && i.StepRepeatCount == 3),
                C3 = g.Count(i => i.State == WorkflowState.Completed && i.StepRepeatCount >= 4),
                Expired = g.Count(i => i.State == WorkflowState.Expired)
            })
            .ToListAsync(ct);

        return raw.Select(r => new ReminderRow(
            r.DefinitionId, r.R0, r.R1, r.R2, r.R3, r.C0, r.C1, r.C2, r.C3, r.Expired))
            .ToList();
    }

    public async Task<IReadOnlyList<IngestionQualityRow>> IngestionQualityAsync(
        Guid projectId, DateRange range, CancellationToken ct = default)
    {
        await EnsureProjectVisibleAsync(projectId, ct);

        return await _db.IngestionBatches
            .Where(b => b.ProjectId == projectId
                     && b.IngestedAt >= range.From && b.IngestedAt < range.To)
            .OrderByDescending(b => b.IngestedAt)
            .Select(b => new IngestionQualityRow(
                b.Id, b.SourceType, b.SourceRef,
                b.TotalRows, b.AcceptedRows, b.RejectedRows,
                b.TotalRows == 0 ? 0 : (double)b.AcceptedRows / b.TotalRows,
                b.Status, b.IngestedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditRow>> AuditTrailAsync(
        Guid projectId, DateRange range, int take, CancellationToken ct = default)
    {
        await EnsureProjectVisibleAsync(projectId, ct);
        var cap = Math.Clamp(take, 1, 5000);

        // SQLite EF translator chokes on DateTimeOffset comparisons against
        // AuditLog specifically (works for other entities — likely a Bytea
        // vs Text mapping quirk under EnsureCreated). Workaround: pull rows
        // by UserId/Project boundary as an unfiltered enumerable, then
        // project/filter client-side. Production uses SQL Server where the
        // translator handles DateTimeOffset natively.
        var from = range.From;
        var to = range.To;
        var allMatching = (await _db.AuditLogs
                .AsNoTracking()
                .Where(a => a.ProjectId == projectId)
                .Take(cap * 4)
                .ToListAsync(ct))
            .Where(a => a.CreatedAt >= from && a.CreatedAt < to)
            .OrderByDescending(a => a.CreatedAt)
            .Take(cap)
            .ToList();

        var rows = allMatching.Select(a => new
        {
            a.CreatedAt, a.UserId, a.Action, a.EntityType, a.EntityId,
            a.IpAddress, a.CorrelationId
        }).ToList();

        if (rows.Count == 0) return Array.Empty<AuditRow>();

        var userIds = rows.Select(r => r.UserId).Where(id => id != Guid.Empty).Distinct().ToList();
        var emails = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.IgnoreQueryFilters()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Email })
                .ToDictionaryAsync(x => x.Id, x => x.Email, ct);

        return rows.Select(r => new AuditRow(
            r.CreatedAt,
            r.UserId,
            emails.GetValueOrDefault(r.UserId),
            r.Action,
            r.EntityType,
            r.EntityId,
            r.IpAddress,
            r.CorrelationId)).ToList();
    }

    public async Task<IReadOnlyList<UserActivityRow>> UserActivityAsync(
        DateRange range, int take, CancellationToken ct = default)
    {
        // Cross-project view — restrict to the user's accessible projects to
        // avoid leaking other tenants' activity. For system admins,
        // AccessibleProjectIdsAsync returns every project.
        var visible = await _me.AccessibleProjectIdsAsync(ct);
        var cap = Math.Clamp(take, 1, 1000);

        // Pre-aggregate counts grouped by user, joining the local Users table
        // for display (Users isn't filtered by project, so visibility is
        // enforced via the ProjectId scope on AuditLogs only).
        var grouped = await _db.AuditLogs
            .AsNoTracking()
            .Where(a => a.CreatedAt >= range.From && a.CreatedAt < range.To
                     && (a.ProjectId == null || visible.Contains(a.ProjectId.Value)))
            .GroupBy(a => a.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                TotalActions = g.Count(),
                Logins               = g.Count(a => a.Action == "auth.api_success"
                                                  || a.Action == "auth.cache_hit"
                                                  || a.Action == "auth.offline_fallback"),
                SmsDispatchInitiated = g.Count(a => a.Action == "sms.dispatch.enqueued"
                                                  || a.Action == "sms.send"),
                IngestionUploads     = g.Count(a => a.Action == "ingestion.batch.complete"),
                ProjectsCreated      = g.Count(a => a.Action == "project.create"),
                LastActiveAt         = (DateTimeOffset?)g.Max(a => a.CreatedAt)
            })
            .OrderByDescending(x => x.TotalActions)
            .Take(cap)
            .ToListAsync(ct);

        if (grouped.Count == 0) return Array.Empty<UserActivityRow>();

        var userIds = grouped.Select(g => g.UserId).ToHashSet();
        var users = await _db.Users.IgnoreQueryFilters()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, ct);

        return grouped.Select(g =>
        {
            users.TryGetValue(g.UserId, out var u);
            return new UserActivityRow(
                g.UserId,
                u?.Email,
                u?.DisplayName,
                g.TotalActions,
                g.Logins,
                g.SmsDispatchInitiated,
                g.IngestionUploads,
                g.ProjectsCreated,
                g.LastActiveAt);
        }).ToList();
    }

    public async Task ExportCsvAsync<TRow>(IEnumerable<TRow> rows, Stream destination, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(destination, leaveOpen: true);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        await csv.WriteRecordsAsync(rows, ct);
    }

    private async Task EnsureProjectVisibleAsync(Guid projectId, CancellationToken ct)
    {
        var visible = await _me.AccessibleProjectIdsAsync(ct);
        if (!visible.Contains(projectId))
            throw new UnauthorizedAccessException(
                $"Project {projectId} not visible to current user.");
    }
}

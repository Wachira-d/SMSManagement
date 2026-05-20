using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Ingestion.Domain;

namespace SMSManagement.Modules.Identity.Controllers;

[ApiController]
[Authorize]
[Route("api/projects")]
public sealed class ProjectsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IProjectAccessService _access;
    private readonly IAuditLogger _audit;

    public ProjectsController(AppDbContext db, ICurrentUser me,
        IProjectAccessService access, IAuditLogger audit)
    {
        _db = db;
        _me = me;
        _access = access;
        _audit = audit;
    }

    public sealed record CreateProjectRequest(
        string Code,
        string Name,
        string? DefaultProvider);

    public sealed record UpdateProjectRequest(
        string? Name = null,
        string? DefaultProvider = null,
        short? ShortlinkSlugLength = null,
        string? ShortlinkAlphabet = null,
        string? NotificationEmails = null,
        string? NotificationSubjectPrefix = null,
        bool? NotifyOnIngestSuccess = null,
        bool? NotifyOnIngestPartial = null,
        bool? NotifyOnIngestFailure = null,
        bool? SmsEnabled = null,
        bool? ShortlinkEnabled = null,
        bool? WorkflowEnabled = null,
        bool? IngestionEnabled = null,
        bool? EmailAlertsEnabled = null,
        string? CouponRedeemDomain = null);

    private static readonly System.Text.RegularExpressions.Regex AlphabetRegex =
        new("^[A-Za-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public sealed record ShareRequest(Guid UserId, ProjectAccessLevel Level);

    /// <summary>List projects visible to the caller (own + shared).</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var meId = _me.UserId;
        var rows = await _db.Projects
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id, p.Code, p.Name, p.DefaultProvider, p.CreatedAt,
                MyAccess = _db.Set<ProjectMembership>()
                    .Where(m => m.ProjectId == p.Id && m.UserId == meId)
                    .Select(m => (ProjectAccessLevel?)m.AccessLevel)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    /// <summary>
    /// Lightweight per-project stats for the dashboard card grid.
    /// Three aggregate queries (SMS last 24h, active workflow instances,
    /// failed batches last 7d) joined client-side rather than 3*N round-trips.
    /// </summary>
    [HttpGet("dashboard-stats")]
    public async Task<IActionResult> DashboardStats(CancellationToken ct)
    {
        var visible = await _me.AccessibleProjectIdsAsync(ct);
        if (visible.Count == 0) return Ok(Array.Empty<object>());

        var since24h = DateTimeOffset.UtcNow.AddDays(-1);
        var since7d  = DateTimeOffset.UtcNow.AddDays(-7);

        var smsCounts = await _db.Set<SMSManagement.Modules.Sms.Domain.SmsMessage>()
            .Where(s => visible.Contains(s.ProjectId) && s.CreatedAt >= since24h)
            .GroupBy(s => s.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var activeStates = new[]
        {
            SMSManagement.Modules.Workflow.Domain.WorkflowState.AwaitingAction,
            SMSManagement.Modules.Workflow.Domain.WorkflowState.ReminderDue,
            SMSManagement.Modules.Workflow.Domain.WorkflowState.Dispatching,
            SMSManagement.Modules.Workflow.Domain.WorkflowState.Scheduled,
            SMSManagement.Modules.Workflow.Domain.WorkflowState.Pending
        };
        var activeInstances = await _db.Set<SMSManagement.Modules.Workflow.Domain.WorkflowInstance>()
            .Where(i => activeStates.Contains(i.State))
            .Join(_db.Set<SMSManagement.Modules.Workflow.Domain.WorkflowDefinition>(),
                  i => i.DefinitionId, d => d.Id, (i, d) => new { i, d.ProjectId })
            .Where(x => visible.Contains(x.ProjectId))
            .GroupBy(x => x.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var failedBatches = await _db.Set<IngestionBatch>()
            .Where(b => visible.Contains(b.ProjectId)
                     && b.IngestedAt >= since7d
                     && b.RejectedRows > 0)
            .GroupBy(b => b.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var smsMap   = smsCounts.ToDictionary(x => x.ProjectId, x => x.Count);
        var instMap  = activeInstances.ToDictionary(x => x.ProjectId, x => x.Count);
        var batchMap = failedBatches.ToDictionary(x => x.ProjectId, x => x.Count);

        var result = visible.Select(id => new
        {
            ProjectId         = id,
            SmsLast24h        = smsMap.GetValueOrDefault(id, 0),
            ActiveInstances   = instMap.GetValueOrDefault(id, 0),
            FailedBatchesWeek = batchMap.GetValueOrDefault(id, 0)
        });
        return Ok(result);
    }

    [HttpGet("{projectId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var p = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return NotFound();

        var members = await _db.Set<ProjectMembership>()
            .Where(m => m.ProjectId == projectId)
            .Join(_db.Users, m => m.UserId, u => u.Id,
                (m, u) => new { u.Id, u.Email, u.DisplayName, m.AccessLevel, m.GrantedAt })
            .ToListAsync(ct);

        return Ok(new
        {
            p.Id, p.Code, p.Name, p.DefaultProvider, p.CreatedAt, p.ArchivedAt,
            p.RunningNumber, p.CouponRedeemDomain,
            Shortlink = new { p.ShortlinkSlugLength, p.ShortlinkAlphabet },
            Features = new
            {
                Sms = p.SmsEnabled,
                Shortlink = p.ShortlinkEnabled,
                Workflow = p.WorkflowEnabled,
                Ingestion = p.IngestionEnabled,
                EmailAlerts = p.EmailAlertsEnabled
            },
            Notifications = new
            {
                Recipients = SMSManagement.Modules.Notifications
                    .IngestionBatchNotifier.ParseRecipients(p.NotificationEmails),
                SubjectPrefix = string.IsNullOrWhiteSpace(p.NotificationSubjectPrefix)
                    ? $"[{p.Name}]"
                    : p.NotificationSubjectPrefix,
                Triggers = new
                {
                    OnIngestSuccess = p.NotifyOnIngestSuccess,
                    OnIngestPartial = p.NotifyOnIngestPartial,
                    OnIngestFailure = p.NotifyOnIngestFailure
                }
            },
            Members = members
        });
    }

    /// <summary>
    /// Create a new project. Requires the cross-project <c>project.create</c>
    /// permission (granted to the CampaignAdmin AD group by default).
    /// The caller automatically becomes the project's Owner.
    /// Project code is unique — repeat requests with the same code return 409.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "project.create")]
    public async Task<IActionResult> Create(
        [FromBody] CreateProjectRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
            return BadRequest("Code and Name are required.");

        var code = req.Code.Trim().ToLowerInvariant();
        if (await _db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Code == code, ct))
            return Conflict(new { Message = $"Project code '{code}' already exists." });

        // Atomic: project + owner membership must succeed together.
        using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Short sequential RunningNumber — used in the shared-domain coupon
        // redeem URL. (max + 1); the unique index is the final guard against
        // a rare concurrent-create race.
        var nextRunning = (await _db.Projects.IgnoreQueryFilters()
            .MaxAsync(p => (int?)p.RunningNumber, ct) ?? 0) + 1;

        var project = new Project
        {
            Code = code,
            Name = req.Name.Trim(),
            RunningNumber = nextRunning,
            DefaultProvider = string.IsNullOrWhiteSpace(req.DefaultProvider)
                ? "etracker"
                : req.DefaultProvider.Trim().ToLowerInvariant()
        };
        _db.Projects.Add(project);

        _db.Set<ProjectMembership>().Add(new ProjectMembership
        {
            ProjectId = project.Id,
            UserId = _me.UserId,
            AccessLevel = ProjectAccessLevel.Owner,
            GrantedByUserId = _me.UserId
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "project.create", "Project", project.Id.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier,
            After: new { project.Code, project.Name, project.DefaultProvider },
            ProjectId: project.Id), ct);

        return CreatedAtAction(nameof(Get), new { projectId = project.Id },
            new { project.Id, project.Code, project.Name, project.DefaultProvider });
    }

    [HttpPut("{projectId:guid}")]
    public async Task<IActionResult> Update(Guid projectId,
        [FromBody] UpdateProjectRequest req, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return NotFound();

        var before = new
        {
            project.Name, project.DefaultProvider,
            project.ShortlinkSlugLength, project.ShortlinkAlphabet,
            project.NotificationEmails,
            project.SmsEnabled, project.ShortlinkEnabled, project.WorkflowEnabled,
            project.IngestionEnabled, project.EmailAlertsEnabled
        };

        if (!string.IsNullOrWhiteSpace(req.Name)) project.Name = req.Name.Trim();
        if (!string.IsNullOrWhiteSpace(req.DefaultProvider))
            project.DefaultProvider = req.DefaultProvider.Trim().ToLowerInvariant();

        if (req.ShortlinkSlugLength is { } len)
        {
            if (len < 4 || len > 16)
                return BadRequest("ShortlinkSlugLength must be between 4 and 16.");
            project.ShortlinkSlugLength = len;
        }

        if (req.ShortlinkAlphabet is not null)
        {
            var raw = req.ShortlinkAlphabet;
            if (raw.Length == 0)
            {
                project.ShortlinkAlphabet = null;   // clear → use global default
            }
            else
            {
                if (raw.Length > 80)
                    return BadRequest("ShortlinkAlphabet must be at most 80 characters.");
                if (!AlphabetRegex.IsMatch(raw))
                    return BadRequest("ShortlinkAlphabet must contain only A-Z a-z 0-9 _ -");
                var unique = new HashSet<char>(raw);   // CASE-SENSITIVE on purpose
                if (unique.Count < 10)
                    return BadRequest(
                        "ShortlinkAlphabet must contain at least 10 unique characters " +
                        "(case-sensitive — 'a' and 'A' count separately).");
                // Normalise: dedupe but preserve original order for reproducibility.
                var deduped = new string(raw.Distinct().ToArray());
                project.ShortlinkAlphabet = deduped;
            }
        }

        if (req.NotificationEmails is not null)
            project.NotificationEmails = string.IsNullOrWhiteSpace(req.NotificationEmails)
                ? null : req.NotificationEmails.Trim();
        if (req.NotificationSubjectPrefix is not null)
            project.NotificationSubjectPrefix = string.IsNullOrWhiteSpace(req.NotificationSubjectPrefix)
                ? null : req.NotificationSubjectPrefix.Trim();
        if (req.NotifyOnIngestSuccess is { } nis) project.NotifyOnIngestSuccess = nis;
        if (req.NotifyOnIngestPartial is { } nip) project.NotifyOnIngestPartial = nip;
        if (req.NotifyOnIngestFailure is { } nif) project.NotifyOnIngestFailure = nif;

        // Feature kill switches — explicit nulls leave them untouched.
        if (req.SmsEnabled        is { } s)  project.SmsEnabled        = s;
        if (req.ShortlinkEnabled  is { } sl) project.ShortlinkEnabled  = sl;
        if (req.WorkflowEnabled   is { } w)  project.WorkflowEnabled   = w;
        if (req.IngestionEnabled  is { } i)  project.IngestionEnabled  = i;
        if (req.EmailAlertsEnabled is { } e) project.EmailAlertsEnabled = e;

        if (req.CouponRedeemDomain is not null)
        {
            var domain = req.CouponRedeemDomain.Trim().TrimEnd('/').ToLowerInvariant();
            if (domain.Length == 0)
            {
                project.CouponRedeemDomain = null;   // clear → shared-domain /r/{n}/{token}
            }
            else
            {
                // Enforce uniqueness in code (no DB unique index — see AppDbContext).
                var clash = await _db.Projects.IgnoreQueryFilters()
                    .AnyAsync(p => p.Id != projectId && p.CouponRedeemDomain == domain, ct);
                if (clash)
                    return Conflict(new { Message =
                        $"Domain '{domain}' is already the coupon domain of another project." });
                project.CouponRedeemDomain = domain;
            }
        }

        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "project.update", "Project", projectId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier,
            Before: before,
            After: new
            {
                project.Name, project.DefaultProvider,
                project.ShortlinkSlugLength, project.ShortlinkAlphabet,
                project.NotificationEmails,
                project.SmsEnabled, project.ShortlinkEnabled, project.WorkflowEnabled,
                project.IngestionEnabled, project.EmailAlertsEnabled
            },
            ProjectId: projectId), ct);

        return NoContent();
    }

    [HttpPost("{projectId:guid}/members")]
    public async Task<IActionResult> Share(Guid projectId,
        [FromBody] ShareRequest req, CancellationToken ct)
    {
        var m = await _access.ShareAsync(projectId, req.UserId, req.Level, ct);
        return Ok(new { m.Id, m.AccessLevel, m.GrantedAt });
    }

    [HttpDelete("{projectId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> Revoke(Guid projectId, Guid userId, CancellationToken ct)
    {
        await _access.RevokeAsync(projectId, userId, ct);
        return NoContent();
    }

    [HttpPost("{projectId:guid}/transfer-ownership")]
    public async Task<IActionResult> Transfer(Guid projectId,
        [FromBody] Guid newOwnerId, CancellationToken ct)
    {
        await _access.TransferOwnershipAsync(projectId, newOwnerId, ct);
        return NoContent();
    }

    /// <summary>
    /// Soft-delete (archive). Project disappears from list endpoints and is
    /// rejected by access checks. Never hard-deleted — child rows (SMS, audit,
    /// shortlinks) keep their FKs valid for compliance retention.
    /// </summary>
    [HttpDelete("{projectId:guid}")]
    public async Task<IActionResult> Archive(Guid projectId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Owner, ct);

        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return NotFound();
        if (project.ArchivedAt is not null) return NoContent();

        project.ArchivedAt = DateTimeOffset.UtcNow;
        project.ArchivedByUserId = _me.UserId;

        // Cascade: terminate non-terminal workflow instances + cancel queued
        // SMS for this project. Without this, reminder SMS keeps dispatching
        // after the project is archived. Single bulk UPDATE — atomic.
        var defIds = await _db.WorkflowDefinitions
            .Where(d => d.ProjectId == projectId).Select(d => d.Id).ToListAsync(ct);
        int cancelledInstances = 0;
        int cancelledSms = 0;
        if (defIds.Count > 0)
        {
            cancelledInstances = await _db.WorkflowInstances
                .Where(i => defIds.Contains(i.DefinitionId)
                         && i.State != Modules.Workflow.Domain.WorkflowState.Completed
                         && i.State != Modules.Workflow.Domain.WorkflowState.Failed
                         && i.State != Modules.Workflow.Domain.WorkflowState.Expired)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.State, Modules.Workflow.Domain.WorkflowState.Expired)
                    .SetProperty(x => x.NextCheckAt, (DateTimeOffset?)null), ct);

            cancelledSms = await _db.SmsMessages
                .Where(m => m.ProjectId == projectId
                         && m.Status == Modules.Sms.Domain.SmsStatus.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, Modules.Sms.Domain.SmsStatus.Failed)
                    .SetProperty(x => x.ErrorCode, "PROJECT_ARCHIVED"), ct);
        }

        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "project.archive", "Project", projectId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier,
            After: new
            {
                ArchivedAt = project.ArchivedAt,
                CancelledInstances = cancelledInstances,
                CancelledSms = cancelledSms
            },
            ProjectId: projectId), ct);
        return NoContent();
    }

    /// <summary>Restore an archived project. System-admin only — owners can't
    /// see their archived projects via the filter, so they can't request this
    /// path on their own.</summary>
    [HttpPost("{projectId:guid}/restore")]
    [Authorize(Policy = "audit.read")]
    public async Task<IActionResult> Restore(Guid projectId, CancellationToken ct)
    {
        var project = await _db.Projects
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return NotFound();
        if (project.ArchivedAt is null) return NoContent();

        project.ArchivedAt = null;
        project.ArchivedByUserId = null;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _me.UserId, "project.restore", "Project", projectId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.TraceIdentifier,
            After: new { RestoredBy = _me.UserId },
            ProjectId: projectId), ct);
        return NoContent();
    }
}

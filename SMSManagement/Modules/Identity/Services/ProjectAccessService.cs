using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Identity.Domain;

namespace SMSManagement.Modules.Identity.Services;

public sealed class ProjectAccessService : IProjectAccessService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;

    public ProjectAccessService(AppDbContext db, ICurrentUser user, IAuditLogger audit)
    {
        _db = db;
        _user = user;
        _audit = audit;
    }

    public async Task<bool> CanAsync(Guid projectId, ProjectAccessLevel required, CancellationToken ct = default)
    {
        if (_user.IsSystemAdmin) return true;

        var level = await _db.Set<ProjectMembership>()
            .Where(m => m.ProjectId == projectId && m.UserId == _user.UserId)
            .Select(m => (ProjectAccessLevel?)m.AccessLevel)
            .FirstOrDefaultAsync(ct);

        return level is not null && level >= required;
    }

    public async Task EnsureAsync(Guid projectId, ProjectAccessLevel required, CancellationToken ct = default)
    {
        if (!await CanAsync(projectId, required, ct))
            throw new UnauthorizedAccessException(
                $"User lacks {required} on project {projectId}.");
    }

    public async Task<ProjectMembership> ShareAsync(
        Guid projectId, Guid targetUserId, ProjectAccessLevel level, CancellationToken ct = default)
    {
        await EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        // Idempotent upsert: re-sharing updates the level instead of stacking rows.
        var existing = await _db.Set<ProjectMembership>()
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == targetUserId, ct);

        var before = existing is null ? null : new { existing.AccessLevel };

        if (existing is null)
        {
            existing = new ProjectMembership
            {
                ProjectId = projectId,
                UserId = targetUserId,
                AccessLevel = level,
                GrantedByUserId = _user.UserId
            };
            _db.Add(existing);
        }
        else
        {
            existing.AccessLevel = level;
            existing.GrantedByUserId = _user.UserId;
            existing.GrantedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _user.UserId, "project.share", "Project", projectId.ToString(),
            string.Empty, string.Empty, string.Empty,
            Before: before,
            After: new { existing.AccessLevel, TargetUserId = targetUserId }), ct);

        return existing;
    }

    public async Task RevokeAsync(Guid projectId, Guid targetUserId, CancellationToken ct = default)
    {
        await EnsureAsync(projectId, ProjectAccessLevel.Admin, ct);

        var membership = await _db.Set<ProjectMembership>()
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == targetUserId, ct);
        if (membership is null) return;

        if (membership.AccessLevel == ProjectAccessLevel.Owner)
            throw new InvalidOperationException(
                "Cannot revoke the Owner. Transfer ownership first.");

        _db.Remove(membership);
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _user.UserId, "project.revoke", "Project", projectId.ToString(),
            string.Empty, string.Empty, string.Empty,
            Before: new { membership.AccessLevel, TargetUserId = targetUserId }), ct);
    }

    public async Task TransferOwnershipAsync(Guid projectId, Guid newOwnerUserId, CancellationToken ct = default)
    {
        await EnsureAsync(projectId, ProjectAccessLevel.Owner, ct);

        // Atomic owner swap — there can be only one Owner per project.
        using var tx = await _db.Database.BeginTransactionAsync(ct);

        var currentOwner = await _db.Set<ProjectMembership>()
            .FirstAsync(m => m.ProjectId == projectId
                          && m.AccessLevel == ProjectAccessLevel.Owner, ct);
        currentOwner.AccessLevel = ProjectAccessLevel.Admin;

        var target = await _db.Set<ProjectMembership>()
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == newOwnerUserId, ct);
        if (target is null)
        {
            target = new ProjectMembership
            {
                ProjectId = projectId,
                UserId = newOwnerUserId,
                AccessLevel = ProjectAccessLevel.Owner,
                GrantedByUserId = _user.UserId
            };
            _db.Add(target);
        }
        else
        {
            target.AccessLevel = ProjectAccessLevel.Owner;
            target.GrantedAt = DateTimeOffset.UtcNow;
            target.GrantedByUserId = _user.UserId;
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            _user.UserId, "project.transfer_ownership", "Project", projectId.ToString(),
            string.Empty, string.Empty, string.Empty,
            After: new { NewOwnerUserId = newOwnerUserId }), ct);
    }
}

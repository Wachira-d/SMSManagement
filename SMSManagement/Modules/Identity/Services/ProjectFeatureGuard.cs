using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Identity.Services;

public sealed class ProjectFeatureGuard : IProjectFeatureGuard
{
    private readonly AppDbContext _db;

    public ProjectFeatureGuard(AppDbContext db) => _db = db;

    public async Task<bool> IsEnabledAsync(
        Guid projectId, ProjectFeature feature, CancellationToken ct = default)
    {
        // One projection — covers every feature flag in one round-trip.
        // Cache key would be (projectId,version) — production should layer
        // IMemoryCache on top, but keep this simple for now.
        var flags = await _db.Projects
            .IgnoreQueryFilters() // background jobs need this too
            .Where(p => p.Id == projectId)
            .Select(p => new
            {
                p.ArchivedAt,
                p.SmsEnabled, p.ShortlinkEnabled, p.WorkflowEnabled,
                p.IngestionEnabled, p.EmailAlertsEnabled
            })
            .FirstOrDefaultAsync(ct);

        if (flags is null) return false;          // project doesn't exist
        if (flags.ArchivedAt is not null) return false; // archived ≠ feature-enabled

        return feature switch
        {
            ProjectFeature.Sms         => flags.SmsEnabled,
            ProjectFeature.Shortlink   => flags.ShortlinkEnabled,
            ProjectFeature.Workflow    => flags.WorkflowEnabled,
            ProjectFeature.Ingestion   => flags.IngestionEnabled,
            ProjectFeature.EmailAlerts => flags.EmailAlertsEnabled,
            _                          => false
        };
    }

    public async Task EnsureAsync(
        Guid projectId, ProjectFeature feature, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(projectId, feature, ct))
            throw new FeatureDisabledException(projectId, feature);
    }
}

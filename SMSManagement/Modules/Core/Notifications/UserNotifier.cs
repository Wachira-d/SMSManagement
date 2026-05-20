using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Core.Notifications;

public sealed class UserNotifier : IUserNotifier
{
    private readonly IHubContext<NotificationHub> _hub;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UserNotifier> _log;

    public UserNotifier(
        IHubContext<NotificationHub> hub,
        IServiceScopeFactory scopeFactory,
        ILogger<UserNotifier> log)
    {
        _hub = hub;
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public async Task ToUserAsync(Guid userId, NotificationPayload payload, CancellationToken ct = default)
    {
        try
        {
            await _hub.Clients.Group($"user:{userId}").SendAsync("notify", payload, ct);
        }
        catch (Exception ex)
        {
            // Hub may not be initialised in tests / when SignalR is bypassed.
            // Don't propagate — the calling business operation already
            // succeeded; missing a toast is a UX downgrade, not a bug.
            _log.LogWarning(ex, "UserNotifier failed for user {UserId}", userId);
        }
    }

    public async Task ToProjectAsync(Guid projectId, NotificationPayload payload, CancellationToken ct = default)
    {
        try
        {
            // Resolve project members AT SEND TIME and fan out to their
            // per-user groups. The earlier design put connections into a
            // "project:{id}" group at hub-connect — but membership snapshotted
            // at connect goes stale: a user removed from the project mid-
            // session kept receiving its notifications until they reconnected.
            // Per-user groups never change (userId is stable), and resolving
            // membership fresh here means a revoked user is dropped instantly.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var memberIds = await db.ProjectMemberships
                .AsNoTracking()
                .Where(m => m.ProjectId == projectId)
                .Select(m => m.UserId)
                .ToListAsync(ct);
            if (memberIds.Count == 0) return;

            var withProject = payload with { ProjectId = projectId };
            var groups = memberIds.Select(id => $"user:{id}").ToArray();
            await _hub.Clients.Groups(groups).SendAsync("notify", withProject, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "UserNotifier failed for project {ProjectId}", projectId);
        }
    }
}

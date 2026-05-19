using Microsoft.AspNetCore.SignalR;

namespace SMSManagement.Modules.Core.Notifications;

public sealed class UserNotifier : IUserNotifier
{
    private readonly IHubContext<NotificationHub> _hub;
    private readonly ILogger<UserNotifier> _log;

    public UserNotifier(IHubContext<NotificationHub> hub, ILogger<UserNotifier> log)
    {
        _hub = hub;
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
            _log.LogDebug(ex, "UserNotifier swallowed exception for user {UserId}", userId);
        }
    }

    public async Task ToProjectAsync(Guid projectId, NotificationPayload payload, CancellationToken ct = default)
    {
        try
        {
            // Stamp ProjectId into the payload so the client can filter or
            // route to a per-project notification surface.
            var withProject = payload with { ProjectId = projectId };
            await _hub.Clients.Group($"project:{projectId}").SendAsync("notify", withProject, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "UserNotifier swallowed exception for project {ProjectId}", projectId);
        }
    }
}

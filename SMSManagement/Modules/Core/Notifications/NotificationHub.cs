using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SMSManagement.Modules.Identity.Services;

namespace SMSManagement.Modules.Core.Notifications;

/// <summary>
/// SignalR hub for live operator notifications. The JWT bearer cookie already
/// authenticates the connection (see Program.cs hub mapping), so OnConnected
/// here just routes the caller into their single stable per-user group.
///
/// Group naming convention:
///   user:{guid}  — single-user inbox.
///
/// There is intentionally NO "project:{guid}" group. Project membership
/// changes over a session's lifetime, and a group snapshotted at connect
/// goes stale (a removed user keeps receiving the project's pushes).
/// Instead UserNotifier.ToProjectAsync resolves the project's current
/// members at SEND time and fans out to their user:{guid} groups — which
/// never change, so a revoked member is dropped instantly.
///
/// Events the server sends:
///   "notify"  — JSON payload { kind, title, body, variant, projectId? }
/// </summary>
[Authorize]
public sealed class NotificationHub : Hub
{
    private readonly ICurrentUser _me;

    public NotificationHub(ICurrentUser me) => _me = me;

    public override async Task OnConnectedAsync()
    {
        if (_me.UserId != Guid.Empty)
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{_me.UserId}");
        await base.OnConnectedAsync();
    }
}

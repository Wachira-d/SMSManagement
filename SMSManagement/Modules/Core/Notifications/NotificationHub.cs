using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SMSManagement.Modules.Identity.Services;

namespace SMSManagement.Modules.Core.Notifications;

/// <summary>
/// SignalR hub for live operator notifications. The JWT bearer cookie already
/// authenticates the connection (see Program.cs hub mapping), so OnConnected
/// here just routes the caller into per-user + per-project groups.
///
/// Group naming convention:
///   user:{guid}      — single-user inbox (account locked, password reset issued)
///   project:{guid}   — every member of a project (batch complete, workflow expired)
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

        // Project-scoped pushes only land on members. We pull the visible set
        // once at connect; if memberships change mid-session the client will
        // pick them up on next reconnect / page reload — acceptable for v1.
        foreach (var projectId in await _me.AccessibleProjectIdsAsync(Context.ConnectionAborted))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"project:{projectId}");

        await base.OnConnectedAsync();
    }
}

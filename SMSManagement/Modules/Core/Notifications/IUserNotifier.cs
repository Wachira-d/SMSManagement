namespace SMSManagement.Modules.Core.Notifications;

/// <summary>
/// Push live notifications to operator browsers (SignalR). Hand-rolled so
/// callers in IngestionPipeline / Authenticator / etc. don't have to know
/// about IHubContext or group naming. All calls are fire-and-forget — they
/// must not fail the business operation if SignalR is unconfigured / no
/// connections exist.
/// </summary>
public interface IUserNotifier
{
    /// <summary>Send to a specific user (account locked, reset issued).</summary>
    Task ToUserAsync(Guid userId, NotificationPayload payload, CancellationToken ct = default);

    /// <summary>Send to every connected member of a project.</summary>
    Task ToProjectAsync(Guid projectId, NotificationPayload payload, CancellationToken ct = default);
}

/// <summary>
/// Notification body. <c>Variant</c> drives the browser toast colour
/// (success / info / warning / danger); <c>Kind</c> is a machine code so
/// future clients can switch on it (e.g. open the right modal).
/// </summary>
public sealed record NotificationPayload(
    string Kind,
    string Title,
    string Body,
    string Variant = "info",
    Guid? ProjectId = null,
    object? Data = null);

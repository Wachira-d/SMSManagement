namespace SMSManagement.Modules.Shortlink.Services;

public interface IShortlinkService
{
    Task<string> CreateAsync(
        Guid projectId,
        string targetUrl,
        Guid? workflowInstanceId,
        TimeSpan? lifetime,
        int? maxClicks,
        string? recipientPhone = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the slug of an existing, still-usable shortlink for this
    /// recipient + target URL, or creates one. Reuse happens at two levels so
    /// every reminder for the same (recipient, URL) carries the SAME slug:
    /// (1) same <paramref name="workflowInstanceId"/> — covers self-loop
    ///     reminders inside a single workflow run;
    /// (2) same project + <paramref name="recipientPhone"/> — covers
    ///     cross-instance reminders where the operator uploads a fresh file
    ///     and a brand-new WorkflowInstance is created for the same phone.
    /// On a cross-instance reuse the existing row's WorkflowInstanceId is
    /// updated to the new instance so a click signals the active reminder.
    /// </summary>
    Task<string> GetOrCreateForInstanceAsync(
        Guid projectId,
        string targetUrl,
        Guid workflowInstanceId,
        string? recipientPhone,
        TimeSpan? lifetime,
        CancellationToken ct = default);

    Task<ResolveResult?> ResolveAndRecordAsync(
        string slug,
        string clientIp,
        string? userAgent,
        CancellationToken ct = default);
}

public sealed record ResolveResult(string TargetUrl, Guid ShortlinkId, Guid? WorkflowInstanceId);

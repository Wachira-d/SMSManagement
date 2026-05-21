namespace SMSManagement.Modules.Shortlink.Services;

public interface IShortlinkService
{
    Task<string> CreateAsync(
        Guid projectId,
        string targetUrl,
        Guid? workflowInstanceId,
        TimeSpan? lifetime,
        int? maxClicks,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the slug of an existing, still-usable shortlink for the same
    /// (<paramref name="workflowInstanceId"/>, <paramref name="targetUrl"/>)
    /// pair, or creates one. A reminder re-send pointing at the same URL
    /// therefore carries the SAME shortlink every round instead of minting a
    /// new slug each time.
    /// </summary>
    Task<string> GetOrCreateForInstanceAsync(
        Guid projectId,
        string targetUrl,
        Guid workflowInstanceId,
        TimeSpan? lifetime,
        CancellationToken ct = default);

    Task<ResolveResult?> ResolveAndRecordAsync(
        string slug,
        string clientIp,
        string? userAgent,
        CancellationToken ct = default);
}

public sealed record ResolveResult(string TargetUrl, Guid ShortlinkId, Guid? WorkflowInstanceId);

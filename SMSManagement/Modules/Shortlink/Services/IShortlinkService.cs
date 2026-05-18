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

    Task<ResolveResult?> ResolveAndRecordAsync(
        string slug,
        string clientIp,
        string? userAgent,
        CancellationToken ct = default);
}

public sealed record ResolveResult(string TargetUrl, Guid ShortlinkId, Guid? WorkflowInstanceId);

namespace SMSManagement.Modules.Identity.Services;

/// <summary>
/// One-stop check for "is feature X enabled on project Y?".
/// Throws <see cref="FeatureDisabledException"/> when not — controllers turn
/// that into 409 Conflict via a filter (see <c>FeatureDisabledFilter</c>).
/// </summary>
public interface IProjectFeatureGuard
{
    Task EnsureAsync(Guid projectId, ProjectFeature feature, CancellationToken ct = default);
    Task<bool> IsEnabledAsync(Guid projectId, ProjectFeature feature, CancellationToken ct = default);
}

public enum ProjectFeature
{
    Sms,
    Shortlink,
    Workflow,
    Ingestion,
    EmailAlerts
}

public sealed class FeatureDisabledException : Exception
{
    public ProjectFeature Feature { get; }
    public Guid ProjectId { get; }

    public FeatureDisabledException(Guid projectId, ProjectFeature feature)
        : base($"Feature '{feature}' is disabled for project {projectId}.")
    {
        ProjectId = projectId;
        Feature = feature;
    }
}

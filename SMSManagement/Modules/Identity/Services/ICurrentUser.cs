namespace SMSManagement.Modules.Identity.Services;

/// <summary>Scoped accessor for the authenticated user — resolved from the JWT.</summary>
public interface ICurrentUser
{
    Guid UserId { get; }
    string Email { get; }
    bool IsSystemAdmin { get; }
    /// <summary>Set of project IDs the user has *any* access to. Cached per request.</summary>
    Task<IReadOnlySet<Guid>> AccessibleProjectIdsAsync(CancellationToken ct = default);
}

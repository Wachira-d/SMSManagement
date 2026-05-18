namespace SMSManagement.Modules.Identity.Domain;

/// <summary>
/// Local mirror of the identity record. The source of truth is the external
/// IdP (Azure AD / Auth0) — this row exists so we can reference user IDs in
/// FK relations (memberships, audit logs) and store app-side preferences.
/// </summary>
public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>OIDC "sub" claim — stable across email changes.</summary>
    public string ExternalSubject { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";     // Active | Suspended | Deleted
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public enum ProjectAccessLevel
{
    /// <summary>Read-only — reports, audit, shortlink stats. Cannot dispatch SMS.</summary>
    Viewer = 0,
    /// <summary>Can ingest data, start workflows, dispatch SMS within the project.</summary>
    Member = 1,
    /// <summary>Member + can edit workflow definitions, mappings, and share project.</summary>
    Admin = 2,
    /// <summary>Admin + can delete project / change owner.</summary>
    Owner = 3
}

/// <summary>
/// Many-to-many between Users and Projects with an explicit access level.
/// A user only sees a project if a membership row exists.
/// </summary>
public sealed class ProjectMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public ProjectAccessLevel AccessLevel { get; set; } = ProjectAccessLevel.Member;
    public DateTimeOffset GrantedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid GrantedByUserId { get; set; }
}

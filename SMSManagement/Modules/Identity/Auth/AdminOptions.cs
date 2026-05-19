namespace SMSManagement.Modules.Identity.Auth;

/// <summary>
/// System administration knobs. Operators can be granted system_admin either
/// via an AD group membership (matches <see cref="SystemAdminGroup"/>) or via
/// the per-user <c>IsSystemAdmin</c> flag on the local Users row.
/// </summary>
public sealed class AdminOptions
{
    /// <summary>AD group name. Members are auto-granted role=system_admin
    /// on every JWT issue. Blank = no group-based admin.</summary>
    public string SystemAdminGroup { get; init; } = "SystemAdministrators";
}

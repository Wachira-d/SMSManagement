using Hangfire.Dashboard;

namespace SMSManagement.Infrastructure.Configuration;

/// <summary>
/// Restricts the Hangfire dashboard at /jobs to authenticated users
/// holding the audit.read permission (system admins / SecOps).
/// Without this filter the dashboard is anonymous-readable, which
/// would leak job arguments — including masked recipients — to anyone
/// who can reach the URL.
/// </summary>
public sealed class HangfireAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();
        if (http?.User?.Identity?.IsAuthenticated != true) return false;
        return http.User.HasClaim("perm", "audit.read")
            || http.User.HasClaim("role", "system_admin");
    }
}

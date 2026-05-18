namespace SMSManagement.Modules.Identity.Services;

/// <summary>
/// Pure-data view of the caller, safe to inject into <c>AppDbContext</c>
/// without creating a service-resolution cycle. Populated by middleware from
/// the JWT for HTTP requests; falls back to a system identity for background jobs.
/// </summary>
public interface IUserContext
{
    Guid UserId { get; }
    bool IsAuthenticated { get; }
    bool IsSystemAdmin { get; }
}

internal sealed class SystemUserContext : IUserContext
{
    public Guid UserId => Guid.Empty;
    public bool IsAuthenticated => false;
    public bool IsSystemAdmin => true; // background jobs bypass row-level filters
}

internal sealed class HttpUserContext : IUserContext
{
    public HttpUserContext(Microsoft.AspNetCore.Http.IHttpContextAccessor http)
    {
        var principal = http.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            UserId = Guid.Empty;
            IsAuthenticated = false;
            IsSystemAdmin = false;
            return;
        }

        UserId = Guid.TryParse(principal.FindFirst("app_user_id")?.Value, out var id)
            ? id : Guid.Empty;
        IsAuthenticated = UserId != Guid.Empty;
        IsSystemAdmin = principal.HasClaim("role", "system_admin")
                     || principal.HasClaim("perm", "*");
    }

    public Guid UserId { get; }
    public bool IsAuthenticated { get; }
    public bool IsSystemAdmin { get; }
}

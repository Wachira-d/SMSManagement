namespace SMSManagement.Infrastructure.Middleware;

/// <summary>
/// Adds the conservative defaults every public API should be sending:
///   - X-Content-Type-Options: nosniff
///   - X-Frame-Options: DENY                       (we never render in iframes)
///   - Referrer-Policy: no-referrer
///   - X-Permitted-Cross-Domain-Policies: none
///   - Cache-Control: no-store for API responses   (skip /s/{slug} redirect)
///   - Strict-Transport-Security set by UseHsts() in non-dev.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Permitted-Cross-Domain-Policies"] = "none";

            // Skip cache-control on the shortlink redirect — browsers should NOT
            // cache it because we count every click.
            if (!ctx.Request.Path.StartsWithSegments("/s"))
                h["Cache-Control"] = "no-store";

            return Task.CompletedTask;
        });
        await _next(ctx);
    }
}

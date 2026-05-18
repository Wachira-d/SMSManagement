namespace SMSManagement.Modules.Identity.Auth;

/// <summary>
/// Open-redirect defence. A return-URL from a query string is only honoured
/// when it resolves to a local path (no scheme, no host, no protocol-relative).
/// </summary>
public static class SafeRedirect
{
    /// <summary>Returns the URL if local; otherwise <c>fallback</c>.</summary>
    public static string Resolve(string? returnUrl, string fallback = "/")
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return fallback;

        // Reject protocol-relative ("//evil.com") and UNC ("\\evil") forms.
        if (returnUrl.StartsWith("//") || returnUrl.StartsWith("\\\\"))
            return fallback;

        // Must start with a single '/' — that rules out "http://..." and
        // "javascript:..." (anything with a scheme), since none of those start with '/'.
        return returnUrl.StartsWith('/') ? returnUrl : fallback;
    }
}

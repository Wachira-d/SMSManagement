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

        // Protocol-relative ("//evil.com/x") is treated as remote by the host but
        // some redirect helpers slip it through — reject explicitly.
        if (returnUrl.StartsWith("//") || returnUrl.StartsWith("\\\\"))
            return fallback;

        // Absolute URI? remote.
        if (Uri.TryCreate(returnUrl, UriKind.Absolute, out _)) return fallback;

        // Must start with a single '/'.
        return returnUrl.StartsWith('/') ? returnUrl : fallback;
    }
}

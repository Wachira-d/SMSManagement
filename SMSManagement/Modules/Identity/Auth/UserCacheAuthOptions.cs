namespace SMSManagement.Modules.Identity.Auth;

/// <summary>
/// All knobs for the cache-first authenticator. Values are resolved at startup —
/// secrets MUST come from a secret store (Key Vault), never from a committed file.
/// </summary>
public sealed class UserCacheAuthOptions
{
    /// <summary>How long a cached row's PasswordHash is trusted. Default 90 days.</summary>
    public int CacheDurationDays { get; init; } = 90;

    /// <summary>App-wide pepper appended to every password before hashing.
    /// Combined with the per-user Salt. Loaded from Key Vault at startup.</summary>
    public string PasswordSalt { get; init; } = string.Empty;

    /// <summary>Wrong-password attempts before the local account is locked.</summary>
    public int MaxFailedAttempts { get; init; } = 5;

    /// <summary>How long a locked account stays locked.</summary>
    public int LockoutDurationMinutes { get; init; } = 15;

    /// <summary>Issued JWT lifetime. Equivalent to ASP.NET's "Session.Timeout"
    /// in WebForms — kept on this options object so the cache/session config
    /// lives in one place.</summary>
    public int SessionTimeoutHours { get; init; } = 8;

    /// <summary>"Remember Me" refresh-token lifetime.</summary>
    public int RememberMeDurationDays { get; init; } = 30;

    /// <summary>If true (default), API failure with a valid cached hash logs the
    /// user in anyway. Disable for high-security tenants that must never honour
    /// a stale credential when the IdP is unreachable.</summary>
    public bool AllowOfflineFallback { get; init; } = true;
}

/// <summary>External authentication endpoint (AuthenAPI).</summary>
public sealed class AuthenApiOptions
{
    /// <summary>Root URL — the authenticate endpoint path is appended by the client.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Path appended to BaseUrl. Per spec: <c>/api/ldap/authenticate</c>.</summary>
    public string AuthenticatePath { get; init; } = "/api/ldap/authenticate";

    /// <summary>Service-account API key sent in the <c>X-API-Key</c> header.</summary>
    public string ApiKey { get; init; } = string.Empty;

    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Polly retry attempts for transient upstream failures.
    /// Default 1: auth is sensitive — don't burn the timeout budget retrying
    /// a slow IdP. Set to 0 to disable retries entirely.</summary>
    public int RetryAttempts { get; init; } = 1;

    /// <summary>Maximum time per attempt before Polly cancels and (optionally)
    /// retries. Defaults to <see cref="TimeoutSeconds"/> when unset.</summary>
    public int AttemptTimeoutSeconds { get; init; } = 0;
}

/// <summary>Signing config for locally-issued JWTs (post-cache-login bearer tokens).
/// Lifetime is intentionally NOT here — it comes from
/// <see cref="UserCacheAuthOptions.SessionTimeoutHours"/> so both live together.</summary>
public sealed class LocalJwtOptions
{
    public string Issuer { get; init; } = "campaign-api";
    public string Audience { get; init; } = "campaign-api";
    /// <summary>Base64-encoded HMAC-SHA-256 key (>= 32 bytes). From KMS.</summary>
    public string SigningKeyBase64 { get; init; } = string.Empty;
}

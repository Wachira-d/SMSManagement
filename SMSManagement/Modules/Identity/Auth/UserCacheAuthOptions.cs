namespace SMSManagement.Modules.Identity.Auth;

/// <summary>
/// All knobs for the cache-first authenticator. Values are resolved at startup —
/// the salt MUST come from a secret store (Key Vault), never from a committed file.
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

    /// <summary>If true (default), API failure with a valid cached hash logs the
    /// user in anyway. Disable for high-security tenants that must never honour
    /// a stale credential when the IdP is unreachable.</summary>
    public bool AllowOfflineFallback { get; init; } = true;
}

/// <summary>External authentication endpoint (AuthenAPI).</summary>
public sealed class AuthenApiOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    /// <summary>Service-account API key for the AuthenAPI itself. Resolved from KMS.</summary>
    public string ApiKey { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 5;
}

/// <summary>Signing config for locally-issued JWTs (post-cache-login bearer tokens).</summary>
public sealed class LocalJwtOptions
{
    public string Issuer { get; init; } = "campaign-api";
    public string Audience { get; init; } = "campaign-api";
    /// <summary>Base64-encoded HMAC-SHA-256 key (>= 32 bytes). From KMS.</summary>
    public string SigningKeyBase64 { get; init; } = string.Empty;
    public int TokenLifetimeMinutes { get; init; } = 60;
}

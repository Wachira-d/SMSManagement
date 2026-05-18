namespace SMSManagement.Modules.Identity.Domain;

/// <summary>
/// Local cache of an externally-authenticated user (Active Directory / AuthenAPI).
/// Lets us:
///   - skip the external API call when the password is unchanged and cache is fresh
///   - keep logging users in if the external IdP is temporarily unavailable
///   - enforce app-side account lockout independent of upstream
///
/// NOTE: SHA-256 + salt is the algorithm called out in the spec. For passwords
/// at rest a memory-hard KDF (Argon2id / bcrypt) is preferable; the
/// <see cref="Services.IPasswordHasher"/> abstraction lets the algorithm be
/// swapped without touching the authenticator.
/// </summary>
public sealed class UserCache
{
    public int Id { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary>SHA-256 hex (64 chars) of (password + Salt).</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Per-user random salt so two users with the same password get different hashes.</summary>
    public string Salt { get; set; } = string.Empty;

    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? Department { get; set; }
    public string? Title { get; set; }
    public string? EmployeeId { get; set; }

    /// <summary>JSON array of AD group names — ["CampaignAdmin","SmsDispatch"].</summary>
    public string? GroupsJson { get; set; }

    public bool IsEnabled { get; set; } = true;
    public bool IsLocked { get; set; }
    public int FailedAttempts { get; set; }

    public DateTimeOffset? LastFailedLogin { get; set; }
    public DateTimeOffset? LastLogin { get; set; }

    /// <summary>When we last successfully refreshed this row from the upstream API.</summary>
    public DateTimeOffset LastAdSync { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Hard expiry. After this, the cached password hash is never trusted.</summary>
    public DateTimeOffset CacheExpires { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

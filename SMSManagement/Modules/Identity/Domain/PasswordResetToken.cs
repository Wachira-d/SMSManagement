namespace SMSManagement.Modules.Identity.Domain;

/// <summary>
/// Single-use token for the "forgot password" flow on LOCAL accounts only.
/// AD-authenticated users reset upstream (their AD password); this table
/// covers the break-glass admin path and any future portal-managed users.
///
/// Security model:
///   - Raw token (URL-safe random) is sent in the reset email, then thrown
///     away — only the SHA-256 hash sits in the DB.
///   - <see cref="ExpiresAt"/> is the absolute hard limit (default 1h).
///   - <see cref="UsedAt"/> is set the moment the token redeems; a second
///     attempt with the same raw token finds the hash but sees UsedAt != null
///     and rejects. Defeats replay if the email forwards.
///   - We DO NOT store the user's email plaintext on this row — link is by
///     UserCache.Id so leaking the table alone leaks no PII beyond pwd reset
///     intent.
/// </summary>
public sealed class PasswordResetToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int UserCacheId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public string? RequestIp { get; set; }
}

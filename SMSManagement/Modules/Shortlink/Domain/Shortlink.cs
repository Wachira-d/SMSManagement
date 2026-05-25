namespace SMSManagement.Modules.Shortlink.Domain;

public sealed class Shortlink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid? WorkflowInstanceId { get; set; }

    /// <summary>Public URL token (e.g. "k3F9aQ"). Stored as primary lookup key.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Encrypted at rest — the URL may itself reveal PII (e.g. signed survey links).</summary>
    public byte[] EncryptedTargetUrl { get; set; } = Array.Empty<byte>();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? MaxClicks { get; set; }
    public int ClickCount { get; set; }
    public bool Disabled { get; set; }

    /// <summary>HMAC-SHA256 of the recipient phone, salted with the shortlink
    /// IP-hash salt. Lets a reminder send to the same phone+URL — even when
    /// the operator uploads a fresh file (new WorkflowInstance) — reuse the
    /// original slug instead of minting a new one. Null on shortlinks created
    /// outside a workflow (operator API, bulk import).</summary>
    public byte[]? RecipientPhoneHash { get; set; }
}

public sealed class ShortlinkClick
{
    public long Id { get; set; }
    public Guid ShortlinkId { get; set; }

    /// <summary>Salted SHA-256 of the client IP. Raw IP is never stored.</summary>
    public byte[] IpHash { get; set; } = Array.Empty<byte>();
    public string? UserAgent { get; set; }
    public string? DeviceClass { get; set; }
    public string? Country { get; set; }
    public DateTimeOffset ClickedAt { get; set; } = DateTimeOffset.UtcNow;
}

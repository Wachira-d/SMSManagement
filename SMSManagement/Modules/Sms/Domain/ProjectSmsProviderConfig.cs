namespace SMSManagement.Modules.Sms.Domain;

/// <summary>
/// Per-project override of a provider's credentials / endpoint. Stored
/// encrypted (AES-GCM via FieldEncryptor) so neither raw API keys nor SMTP-
/// like passwords sit in plaintext columns. The dispatcher consults a
/// resolver that merges this row with the global <c>IOptions&lt;...&gt;</c>
/// — fields the project doesn't set fall back to global.
///
/// Composite key: (ProjectId, Provider). One row per provider per project;
/// if the same project needs to A/B between two Etracker accounts, that's
/// a future enhancement (split key or version column).
/// </summary>
public sealed class ProjectSmsProviderConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }

    /// <summary>"etracker" | "infobip" (case-insensitive lookup, but stored
    /// in canonical lower-case to match <c>ISmsProvider.Name</c>).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>AES-GCM ciphertext of the per-provider JSON config blob.
    /// Shape mirrors the provider's options class
    /// (e.g. for Etracker: { BaseUrl, Username, Password, DefaultSenderId, DefaultType }).</summary>
    public byte[] EncryptedConfig { get; set; } = Array.Empty<byte>();

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}

namespace SMSManagement.Modules.Notifications;

/// <summary>
/// SMTP delivery settings. Resolved from config / Key Vault.
/// Leave Host blank to disable email globally — the sender becomes a no-op
/// (logged as a warning so misconfiguration is visible).
/// </summary>
public sealed class SmtpOptions
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 587;
    public bool EnableSsl { get; init; } = true;
    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>RFC-5322 "From" address used for every alert.</summary>
    public string FromAddress { get; init; } = "no-reply@example.com";
    public string FromDisplayName { get; init; } = "Campaign Platform";

    /// <summary>Drop-folder mode for local dev: when set, writes .eml files
    /// to this directory instead of opening an SMTP connection.</summary>
    public string? PickupDirectory { get; init; }
}

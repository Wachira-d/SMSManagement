namespace SMSManagement.Modules.Notifications;

/// <summary>
/// Which transport carries outbound email. Bound to the <c>Email</c> section
/// and read through <c>IOptionsMonitor</c>, so switching it from the admin
/// settings page takes effect on the next send without a restart.
/// </summary>
public sealed class EmailOptions
{
    public const string ProviderSmtp  = "smtp";
    public const string ProviderRelay = "relay";

    /// <summary><c>smtp</c> (default, the original behaviour) or <c>relay</c>.
    /// Anything unrecognised falls back to SMTP rather than dropping mail.</summary>
    public string Provider { get; init; } = ProviderSmtp;
}

/// <summary>
/// Connection to an EmailRelayAPI instance (github.com/Wachira-d/EmailRelayAPI).
/// Bound to <c>Email:Relay</c>. Secrets here are stored in SystemSettings and
/// masked on read by AdminSettingsController — every secret-bearing property
/// name ends in Key / Secret so MaskSecrets catches it.
/// </summary>
public sealed class EmailRelayOptions
{
    /// <summary>Relay root, e.g. <c>https://relay.company.local/</c>. The
    /// client path <c>/api/v1/emails</c> is appended.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Client API key (<c>erk_…</c>) with at least the <c>send</c> and
    /// <c>read</c> scopes — <c>read</c> powers the status reconciler and the
    /// connection test.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Only when the relay client has "Require signed requests" on.
    /// Used as its UTF-8 bytes, exactly as the relay does — never decoded.</summary>
    public string? SigningSecret { get; init; }

    /// <summary>The secret typed into the relay client's webhook settings.
    /// Incoming status webhooks must carry a matching X-Relay-Signature.</summary>
    public string? WebhookSecret { get; init; }

    /// <summary>Escape hatch for a relay that has no webhook secret set. Off by
    /// default: the relay also falls back to sending UNSIGNED when it fails to
    /// decrypt a stored secret, and accepting those would let anyone forge a
    /// delivery status.</summary>
    public bool AllowUnsignedWebhooks { get; init; }

    /// <summary>Override the relay client's default sender profile. Blank =
    /// let the relay choose.</summary>
    public string? SenderProfile { get; init; }

    /// <summary>Explicit From. Blank = the sender profile's From, which is the
    /// safe default: the relay rejects any From outside the client's Allowed
    /// From list with a 403.</summary>
    public string? FromAddress { get; init; }
    public string? FromName { get; init; }

    /// <summary>Cap on one call to the relay, retries included. Each attempt
    /// is limited to 20 s, so keep this comfortably above that.</summary>
    public int TimeoutSeconds { get; init; } = 60;
}

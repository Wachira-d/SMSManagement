namespace SMSManagement.Modules.Sms.Webhooks;

/// <summary>
/// HMAC secrets for delivery-receipt webhooks. One key per provider, base64-encoded.
/// Loaded from Key Vault — empty value disables HMAC for that provider in development
/// (the webhook still rejects requests without the headers).
/// </summary>
public sealed class DlrWebhookOptions
{
    public string? EtrackerSecretBase64 { get; init; }
    public string? InfobipSecretBase64 { get; init; }

    /// <summary>
    /// Shared-secret token for the etracker DN callback. etracker does not
    /// HMAC-sign its delivery notifications, so authentication is done by a
    /// token embedded in the DN URL configured on the etracker account
    /// (…/api/sms/dlr/etracker?token=THIS). Loaded from Key Vault.
    /// </summary>
    public string? EtrackerDnToken { get; init; }
}

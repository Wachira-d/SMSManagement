namespace SMSManagement.Modules.Sms.Webhooks;

/// <summary>
/// HMAC secrets for delivery-receipt webhooks. One key per provider, base64-encoded.
/// Loaded from Key Vault — empty value disables HMAC for that provider in development
/// (the webhook still rejects requests without the headers).
/// </summary>
public sealed class DlrWebhookOptions
{
    /// <summary>
    /// Shared-secret token embedded in each provider's delivery-callback URL.
    /// Neither etracker nor Infobip HMAC-signs its delivery callbacks, so
    /// authentication is the token in the URL configured on the provider
    /// account (…/api/sms/dlr/{provider}?token=THIS). Loaded from Key Vault.
    /// </summary>
    public string? EtrackerDnToken { get; init; }
    public string? InfobipDnToken { get; init; }
}

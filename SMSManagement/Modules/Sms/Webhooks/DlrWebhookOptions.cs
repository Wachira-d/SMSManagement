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
}

namespace SMSManagement.Modules.Sms.Webhooks;

/// <summary>
/// Authentication knobs for delivery-receipt webhooks. Neither etracker nor
/// Infobip HMAC-signs its callback, so authentication is a shared secret —
/// configured per provider — which the provider can supply in any of:
///   - <c>?token=…</c> query parameter (or form field for POSTs)
///   - last path segment (e.g. <c>/api/sms/dlr/etracker/THE_SECRET</c>)
///   - <c>X-DN-Token</c> HTTP header
/// As a last fallback, when the provider can't carry a token at all, set
/// <c>EtrackerDnAllowedIps</c> / <c>InfobipDnAllowedIps</c> to the provider's
/// outbound IP range — requests from those addresses are accepted without a
/// token.
/// </summary>
public sealed class DlrWebhookOptions
{
    public string? EtrackerDnToken { get; init; }
    public string? InfobipDnToken { get; init; }

    /// <summary>Allowlist of remote IPs (exact match) that may post etracker
    /// DNs without a token. Use for providers whose portal accepts only a bare
    /// URL with no query string / header customisation.</summary>
    public string[] EtrackerDnAllowedIps { get; init; } = Array.Empty<string>();
    public string[] InfobipDnAllowedIps  { get; init; } = Array.Empty<string>();

    /// <summary>Last-resort escape hatch: accept the DN webhook unconditionally
    /// — no token, no IP check. Only intended for providers whose portal
    /// literally cannot carry a secret or be pinned to known IPs. Enabling
    /// this lets anyone on the internet post status updates for messages whose
    /// provider-side ID they can guess. Default: false.</summary>
    public bool EtrackerDnAllowAnonymous { get; init; }
    public bool InfobipDnAllowAnonymous  { get; init; }
}


using SMSManagement.Modules.Sms.Domain;

namespace SMSManagement.Modules.Sms.Services;

/// <summary>
/// Translates provider-specific status strings (etracker DN words +
/// Infobip group names) into <see cref="SmsStatus"/>. Centralised here so the
/// DN webhook and the pull-status reconciler agree on the same mapping.
/// </summary>
public static class DlrStatusMap
{
    public static SmsStatus Map(string raw) => raw.ToUpperInvariant() switch
    {
        "DELIVERED" or "DELIVERED_TO_HANDSET" => SmsStatus.Delivered,
        "ACCEPTED" or "PROCESSING" or "PENDING" or "PENDING_ENROUTE" => SmsStatus.Sent,
        "EXPIRED" => SmsStatus.Expired,
        "REJECTED" => SmsStatus.Rejected,
        "UNDELIVERED" or "UNDELIVERABLE" or "FAILED" => SmsStatus.Failed,
        _ => SmsStatus.Sent
    };
}

using SMSManagement.Modules.Sms.Domain;

namespace SMSManagement.Modules.Sms.Services;

/// <summary>Result of a pull-status call against a provider.</summary>
/// <param name="Status">Mapped status, or null if the provider couldn't tell.</param>
/// <param name="ErrorCode">For Failed/Rejected/Expired results, the provider's
/// raw failure reason — written into <c>SmsMessage.ErrorCode</c>.</param>
/// <param name="StatusDetail">Verbatim status word + any carrier detail from
/// the provider response (e.g. <c>"DELIVERED"</c>, <c>"UNDELIVERED: phone
/// off"</c>). Stored in <c>SmsMessage.StatusDetail</c> for every status so
/// the report shows an informational reason regardless of success/failure.</param>
/// <param name="CarrierDeliveredAt">Carrier-reported handset-arrival time if
/// the provider's response includes one (Infobip's <c>doneAt</c>). Etracker
/// pull responses don't carry a timestamp so this is null for that provider.</param>
/// <param name="RawPayload">Verbatim provider response body, for the operator
/// to inspect fields we don't yet have typed columns for.</param>
public sealed record StatusQueryResult(
    SmsStatus? Status, string? ErrorCode, string? StatusDetail,
    DateTimeOffset? CarrierDeliveredAt = null, string? RawPayload = null);

/// <summary>
/// Per-provider client that asks "what's the current status of msgId X?" so
/// we can backfill messages where the DN webhook never arrived. One impl per
/// provider, keyed by <see cref="ProviderName"/>.
/// </summary>
public interface IProviderStatusQuery
{
    string ProviderName { get; }

    /// <summary>True if the provider is configured (URL/credentials present)
    /// and the operator has opted in to status pulls. When false, the
    /// reconciler skips this provider — the DN webhook remains the sole
    /// source of truth.</summary>
    bool IsEnabled(Guid projectId);

    Task<StatusQueryResult> QueryAsync(Guid projectId, string providerMessageId, CancellationToken ct);
}

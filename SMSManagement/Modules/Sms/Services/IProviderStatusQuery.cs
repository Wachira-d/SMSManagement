using SMSManagement.Modules.Sms.Domain;

namespace SMSManagement.Modules.Sms.Services;

/// <summary>Result of a pull-status call against a provider.</summary>
/// <param name="Status">Mapped status, or null if the provider couldn't tell.</param>
/// <param name="ErrorCode">For Failed/Rejected/Expired results, the provider's
/// raw failure reason — written into <c>SmsMessage.ErrorCode</c>.</param>
public sealed record StatusQueryResult(SmsStatus? Status, string? ErrorCode);

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

using SMSManagement.Modules.Sms.Domain;

namespace SMSManagement.Modules.Sms.Providers;

/// <summary>
/// Pluggable provider abstraction. Implementations MUST be:
///  - fully async (no blocking calls)
///  - safe for use as a singleton (no per-call state)
///  - free of credential material in URLs/logs
/// </summary>
public interface ISmsProvider
{
    /// <summary>Stable name used to look up provider config (e.g. "etracker", "twilio").</summary>
    string Name { get; }

    Task<ProviderDispatchResult> DispatchAsync(SmsRequest request, CancellationToken ct);

    /// <summary>
    /// Verifies the project's resolved credentials against the provider
    /// without sending a real (deliverable) message. Used by the "test
    /// connection" action on the provider-config UI.
    /// </summary>
    Task<ProviderTestResult> TestCredentialsAsync(Guid projectId, CancellationToken ct = default);
}

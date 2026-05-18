using SMSManagement.Modules.Sms.Domain;

namespace SMSManagement.Modules.Sms.Providers;

/// <summary>
/// Resolves which <see cref="ISmsProvider"/> to use for a given dispatch.
/// Centralising this means swapping providers (Etracker → Infobip) is a
/// config change, never a code change to callers.
/// </summary>
public interface IProviderRouter
{
    ISmsProvider Resolve(SmsRequest request);

    /// <summary>Failover candidate when the primary trips its circuit / returns terminal error.</summary>
    ISmsProvider? Fallback(SmsRequest request, string failedProviderName);
}

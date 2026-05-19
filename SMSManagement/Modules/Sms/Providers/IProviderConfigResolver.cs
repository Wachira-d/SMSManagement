using SMSManagement.Modules.Sms.Providers;

namespace SMSManagement.Modules.Sms.Providers;

/// <summary>
/// Resolves a provider's effective configuration for a given project.
/// "Effective" = per-project override merged on top of the global
/// <c>IOptions&lt;T&gt;</c> from configuration. Fields the project doesn't
/// set keep the global value; fields it does set win.
/// </summary>
public interface IProviderConfigResolver
{
    Task<EtrackerOptions> ResolveEtrackerAsync(Guid projectId, CancellationToken ct = default);
    Task<InfobipOptions>  ResolveInfobipAsync(Guid projectId, CancellationToken ct = default);
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Sms.Domain;

namespace SMSManagement.Modules.Sms.Providers;

public sealed class ProviderRoutingOptions
{
    /// <summary>Default provider when project has no override.</summary>
    public string DefaultProvider { get; init; } = "etracker";

    /// <summary>Ordered failover chain — first entry is primary, rest are tried in order.</summary>
    public string[] FailoverChain { get; init; } = ["etracker", "infobip"];

    /// <summary>
    /// Percentage of traffic to mirror onto the candidate provider (0..100).
    /// Used during a migration window to validate Infobip before the cutover.
    /// </summary>
    public int CandidateTrafficPercent { get; init; }

    public string? CandidateProvider { get; init; }
}

/// <summary>
/// Resolution order:
///   1. Per-project override (Projects.DefaultProvider) — set by Admin in the UI.
///   2. Canary split for migration (e.g. 5% on Infobip, 95% on Etracker).
///   3. Global default from configuration.
/// Failover follows the configured chain, skipping the failed provider.
/// </summary>
public sealed class ProviderRouter : IProviderRouter
{
    private readonly IReadOnlyDictionary<string, ISmsProvider> _providers;
    private readonly IOptionsMonitor<ProviderRoutingOptions> _opts;
    private readonly AppDbContext _db;

    public ProviderRouter(
        IEnumerable<ISmsProvider> providers,
        IOptionsMonitor<ProviderRoutingOptions> opts,
        AppDbContext db)
    {
        _providers = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _opts = opts;
        _db = db;
    }

    public ISmsProvider Resolve(SmsRequest request)
    {
        var name = ResolveName(request);
        if (!_providers.TryGetValue(name, out var p))
            throw new InvalidOperationException(
                $"Provider '{name}' is configured but not registered in DI.");
        return p;
    }

    public ISmsProvider? Fallback(SmsRequest request, string failedProviderName)
    {
        foreach (var name in _opts.CurrentValue.FailoverChain)
        {
            if (name.Equals(failedProviderName, StringComparison.OrdinalIgnoreCase)) continue;
            if (_providers.TryGetValue(name, out var p)) return p;
        }
        return null;
    }

    private string ResolveName(SmsRequest request)
    {
        // 1) Project override — cheap synchronous lookup (router is scoped, cached per request).
        var projectDefault = _db.Projects
            .Where(p => p.Id == request.ProjectId)
            .Select(p => p.DefaultProvider)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(projectDefault))
            return projectDefault;

        var opts = _opts.CurrentValue;

        // 2) Canary split — deterministic based on recipient hash so the same user
        //    always hits the same provider (no per-message flapping).
        if (opts is { CandidateProvider: { } cand, CandidateTrafficPercent: > 0 })
        {
            var bucket = Math.Abs(request.Recipient.GetHashCode()) % 100;
            if (bucket < opts.CandidateTrafficPercent) return cand;
        }

        return opts.DefaultProvider;
    }
}

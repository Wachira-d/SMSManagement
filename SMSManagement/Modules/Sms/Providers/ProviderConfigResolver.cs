using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;

namespace SMSManagement.Modules.Sms.Providers;

public sealed class ProviderConfigResolver : IProviderConfigResolver
{
    private readonly AppDbContext _db;
    private readonly FieldEncryptor _crypto;
    private readonly IOptions<EtrackerOptions> _etrackerDefaults;
    private readonly IOptions<InfobipOptions>  _infobipDefaults;
    private readonly ILogger<ProviderConfigResolver> _log;

    public ProviderConfigResolver(
        AppDbContext db,
        FieldEncryptor crypto,
        IOptions<EtrackerOptions> etrackerDefaults,
        IOptions<InfobipOptions> infobipDefaults,
        ILogger<ProviderConfigResolver> log)
    {
        _db = db;
        _crypto = crypto;
        _etrackerDefaults = etrackerDefaults;
        _infobipDefaults = infobipDefaults;
        _log = log;
    }

    public Task<EtrackerOptions> ResolveEtrackerAsync(Guid projectId, CancellationToken ct = default)
        => MergeAsync("etracker", projectId, _etrackerDefaults.Value, Merge, ct);

    public Task<InfobipOptions> ResolveInfobipAsync(Guid projectId, CancellationToken ct = default)
        => MergeAsync("infobip", projectId, _infobipDefaults.Value, Merge, ct);

    // ---------- internals ----------

    private async Task<T> MergeAsync<T>(
        string provider, Guid projectId, T globalDefaults,
        Func<T, T, T> mergeFn, CancellationToken ct)
        where T : class
    {
        var row = await _db.Set<Domain.ProjectSmsProviderConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.Provider == provider, ct);

        if (row is null) return globalDefaults;

        T? perProject;
        try
        {
            var json = _crypto.Decrypt(row.EncryptedConfig);
            perProject = JsonSerializer.Deserialize<T>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Per-project {Provider} config for {ProjectId} could not be decrypted/parsed; falling back to global.",
                provider, projectId);
            return globalDefaults;
        }

        return perProject is null ? globalDefaults : mergeFn(globalDefaults, perProject);
    }

    // Per-field merge: blanks in the project row fall back to the global default.
    // Numerics: 0/default counts as "use global"; non-zero overrides.
    private static EtrackerOptions Merge(EtrackerOptions g, EtrackerOptions p) => new()
    {
        BaseUrl         = Pick(p.BaseUrl,         g.BaseUrl),
        Username        = Pick(p.Username,        g.Username),
        Password        = Pick(p.Password,        g.Password),
        DefaultSenderId = Pick(p.DefaultSenderId, g.DefaultSenderId),
        DefaultType     = Pick(p.DefaultType,     g.DefaultType)
    };

    private static InfobipOptions Merge(InfobipOptions g, InfobipOptions p) => new()
    {
        BaseUrl         = Pick(p.BaseUrl,         g.BaseUrl),
        ApiKey          = Pick(p.ApiKey,          g.ApiKey),
        DefaultSenderId = Pick(p.DefaultSenderId, g.DefaultSenderId)
    };

    private static string Pick(string? primary, string fallback) =>
        string.IsNullOrWhiteSpace(primary) ? fallback : primary;
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Core.Logging;

/// <summary>
/// Hangfire daily job that drops ErrorLogs entries older than the retention
/// window. Both the window and whether it runs at all are configurable via
/// the <c>ErrorLogRetention</c> section — set <c>PurgeEnabled=false</c> to
/// keep every log row (no automatic clearing).
/// </summary>
public interface IErrorLogPurger
{
    int RetentionDays { get; }
    Task<int> PurgeAsync(CancellationToken ct = default);
}

public sealed class ErrorLogPurger : IErrorLogPurger
{
    private readonly AppDbContext _db;
    private readonly ErrorLogRetentionOptions _opts;
    private readonly ILogger<ErrorLogPurger> _log;

    public int RetentionDays => Math.Max(1, _opts.RetentionDays);

    public ErrorLogPurger(
        AppDbContext db, IOptions<ErrorLogRetentionOptions> opts, ILogger<ErrorLogPurger> log)
    {
        _db = db;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        if (!_opts.PurgeEnabled)
        {
            _log.LogInformation(
                "ErrorLog purge disabled (ErrorLogRetention:PurgeEnabled=false) — keeping all rows.");
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
        var deleted = await _db.ErrorLogs
            .Where(e => e.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        if (deleted > 0)
            _log.LogInformation("ErrorLog purge: removed {Count} rows older than {Days} days.",
                deleted, RetentionDays);
        return deleted;
    }
}

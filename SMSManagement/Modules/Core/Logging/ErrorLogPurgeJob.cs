using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Core.Logging;

/// <summary>
/// Hangfire daily job that drops ErrorLogs entries older than the retention
/// window (default 30 days). Production retention should follow your incident
/// response policy — bump <see cref="RetentionDays"/> in DI if needed.
/// </summary>
public interface IErrorLogPurger
{
    int RetentionDays { get; }
    Task<int> PurgeAsync(CancellationToken ct = default);
}

public sealed class ErrorLogPurger : IErrorLogPurger
{
    private readonly AppDbContext _db;
    private readonly ILogger<ErrorLogPurger> _log;

    public int RetentionDays => 30;

    public ErrorLogPurger(AppDbContext db, ILogger<ErrorLogPurger> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
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

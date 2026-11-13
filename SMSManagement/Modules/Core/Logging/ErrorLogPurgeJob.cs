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
    private readonly IOptionsMonitor<ErrorLogRetentionOptions> _opts;
    private readonly ILogger<ErrorLogPurger> _log;

    public int RetentionDays => Math.Max(1, _opts.CurrentValue.RetentionDays);

    public ErrorLogPurger(
        AppDbContext db, IOptionsMonitor<ErrorLogRetentionOptions> opts, ILogger<ErrorLogPurger> log)
    {
        _db = db;
        _opts = opts;
        _log = log;
    }

    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        if (!_opts.CurrentValue.PurgeEnabled)
        {
            _log.LogInformation(
                "ErrorLog purge disabled (ErrorLogRetention:PurgeEnabled=false) — keeping all rows.");
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
        var deletedErrors = await _db.ErrorLogs
            .Where(e => e.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        // DN log volume is much higher than error logs (1+ row per SMS) but
        // the same retention window applies — sharing the toggle keeps ops
        // configuration simple.
        var deletedDn = await _db.DnLogs
            .Where(e => e.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        // Email webhook calls and the outbound-email log follow the same window.
        var deletedHooks = await _db.EmailWebhookLogs
            .Where(e => e.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        var deletedEmails = await _db.OutboundEmails
            .Where(e => e.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        var total = deletedErrors + deletedDn + deletedHooks + deletedEmails;
        if (total > 0)
            _log.LogInformation(
                "Log purge: removed {Errors} ErrorLogs, {Dn} DnLogs, {Hooks} EmailWebhookLogs and {Emails} OutboundEmails rows older than {Days} days.",
                deletedErrors, deletedDn, deletedHooks, deletedEmails, RetentionDays);
        return total;
    }
}

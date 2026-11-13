using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Notifications.Relay;

namespace SMSManagement.Modules.Notifications;

/// <summary>Send and get the tracked outcome back — for the admin test
/// button and anything else that has to show the user what happened.</summary>
public interface IEmailDispatcher
{
    /// <summary>Never throws for a delivery problem. Returns the
    /// <see cref="OutboundEmail"/> row as saved (or, if the database write
    /// failed, an unsaved copy describing the attempt).</summary>
    Task<OutboundEmail> SendTrackedAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>
/// The one <see cref="IEmailSender"/>: writes an <see cref="OutboundEmail"/>
/// row, hands the message to the transport chosen by <c>Email:Provider</c>,
/// and records what the transport said. Singleton (as SmtpEmailSender was),
/// so it opens a scope per send for the DbContext.
///
/// The row is written BEFORE the transport call so that a relay webhook
/// arriving faster than our own save can still find it by idempotency key.
/// </summary>
public sealed class EmailDispatcher : IEmailSender, IEmailDispatcher
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<EmailOptions> _opts;
    private readonly IEnumerable<IEmailTransport> _transports;
    private readonly TimeProvider _clock;
    private readonly ILogger<EmailDispatcher> _log;

    public EmailDispatcher(IServiceScopeFactory scopes, IOptionsMonitor<EmailOptions> opts,
        IEnumerable<IEmailTransport> transports, TimeProvider clock, ILogger<EmailDispatcher> log)
    {
        _scopes = scopes;
        _opts = opts;
        _transports = transports;
        _clock = clock;
        _log = log;
    }

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        => SendTrackedAsync(message, ct);

    /// <summary>The transport <c>Email:Provider</c> names; SMTP for anything
    /// unrecognised so a typo degrades to the old behaviour, not silence.</summary>
    public IEmailTransport ResolveTransport()
    {
        var wanted = (_opts.CurrentValue.Provider ?? string.Empty).Trim();
        return _transports.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? _transports.First(t => t.Name == EmailOptions.ProviderSmtp);
    }

    public async Task<OutboundEmail> SendTrackedAsync(EmailMessage message, CancellationToken ct = default)
    {
        var transport = ResolveTransport();
        var now = _clock.GetUtcNow();
        var row = new OutboundEmail
        {
            CreatedAt = now,
            Provider = transport.Name,
            Category = Cap(message.Category, 64),
            Recipients = Cap(string.Join(", ", message.To), 2000) ?? string.Empty,
            Subject = Cap(message.Subject, 500) ?? string.Empty,
            AttachmentCount = message.Attachments?.Count ?? 0,
            Status = OutboundEmailStatus.Pending,
            StatusSource = "send",
            StatusUpdatedAt = now,
        };
        row.IdempotencyKey = $"smsmgmt-{row.Id:N}";

        if (message.To.Count == 0)
        {
            // Callers already guard this; nothing to log or send.
            row.Status = OutboundEmailStatus.Skipped;
            row.LastError = "No recipients.";
            return row;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tracked = await TrySaveAsync(db, row, isInsert: true, ct);

        EmailSendResult result;
        try
        {
            result = await transport.SendAsync(message, row.IdempotencyKey, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Transports are written not to throw, but this is the last line
            // before a business operation would see it.
            _log.LogError(ex, "Email transport {Transport} threw", transport.Name);
            result = new EmailSendResult(OutboundEmailStatus.Failed, Error: $"{ex.GetType().Name}: {ex.Message}");
        }

        // The webhook may already have moved the row on (fast relay, slow
        // save) — re-read it and merge, rather than overwrite a "sent".
        // A webhook landing in the few ms between this reload and the save
        // below can still lose; the reconciler's next pull repairs that, so
        // a concurrency token isn't worth the retry loop it would need.
        if (tracked)
        {
            try { await db.Entry(row).ReloadAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Reload of email {Id} failed", row.Id); }
        }

        row.RelayMessageId ??= result.RelayMessageId;
        if (row.Status is OutboundEmailStatus.Pending)
        {
            row.Status = result.Status;
            row.StatusSource = "send";
            row.StatusUpdatedAt = _clock.GetUtcNow();
            row.LastError = Cap(result.Error, 2000);
            if (result.Status == OutboundEmailStatus.Sent) row.SentAt = result.SentAt ?? _clock.GetUtcNow();
        }
        else if (result.Error is not null && row.Status != OutboundEmailStatus.Sent)
        {
            row.LastError ??= Cap(result.Error, 2000);
        }

        if (tracked) await TrySaveAsync(db, row, isInsert: false, ct);
        return row;
    }

    private async Task<bool> TrySaveAsync(AppDbContext db, OutboundEmail row, bool isInsert, CancellationToken ct)
    {
        try
        {
            if (isInsert) db.OutboundEmails.Add(row);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A missing table (migration not yet applied) or a DB outage must
            // not stop the email itself — send it untracked.
            _log.LogError(ex, "Could not {Op} outbound email log row {Id}; continuing untracked",
                isInsert ? "insert" : "update", row.Id);
            if (isInsert) db.Entry(row).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            return false;
        }
    }

    private static string? Cap(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

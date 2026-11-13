using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Notifications.Relay;

public interface IEmailRelayReconciler
{
    /// <summary>Pull the current status of relay emails still in flight.
    /// Runs one at a time: a slow relay must not let two runs pull and save
    /// the same rows.</summary>
    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
    Task<int> ReconcileAsync(CancellationToken ct = default);

    /// <summary>Pull one email now (the "Refresh" button). Returns the outcome
    /// text, or an error description.</summary>
    Task<string> RefreshAsync(Guid outboundEmailId, CancellationToken ct = default);
}

/// <summary>
/// Backstop for lost webhooks: the relay tries a webhook three times within
/// about seven seconds, then gives up — an IIS recycle or a deploy here is
/// enough to miss it. Every 10 minutes this asks the relay
/// (<c>GET /api/v1/emails/{id}</c>, needs the <c>read</c> scope) about relay
/// emails that are still Queued/Scheduled/Sending, or Failed recently (the
/// relay may have requeued them).
/// </summary>
public sealed class EmailRelayReconciler : IEmailRelayReconciler
{
    /// <summary>Give the webhook a head start before polling.</summary>
    public static readonly TimeSpan MinAge = TimeSpan.FromMinutes(5);
    /// <summary>Don't ask about the same email more than this often. Just
    /// under the 10-minute job period, so start-time jitter can't make a row
    /// skip every other run.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(9);
    /// <summary>Failed rows are only re-checked to catch a relay requeue, so
    /// they're asked about rarely and only for a day.</summary>
    public static readonly TimeSpan FailedCooldown = TimeSpan.FromHours(1);
    public static readonly TimeSpan FailedMaxAge = TimeSpan.FromDays(1);
    /// <summary>After this, stop asking — the relay's own retries are long over.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);
    /// <summary>A Pending row this old never got an answer from the transport
    /// (process killed mid-send).</summary>
    public static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(30);
    private const int BatchSize = 200;

    private static readonly OutboundEmailStatus[] InFlight =
    {
        OutboundEmailStatus.Pending, OutboundEmailStatus.Queued, OutboundEmailStatus.Scheduled,
        OutboundEmailStatus.Sending, OutboundEmailStatus.Failed
    };

    private readonly AppDbContext _db;
    private readonly EmailRelayClient _client;
    private readonly TimeProvider _clock;
    private readonly ILogger<EmailRelayReconciler> _log;

    public EmailRelayReconciler(AppDbContext db, EmailRelayClient client, TimeProvider clock,
        ILogger<EmailRelayReconciler> log)
    {
        _db = db;
        _client = client;
        _clock = clock;
        _log = log;
    }

    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        if (!_client.IsConfigured(out _)) return 0;
        var now = _clock.GetUtcNow();

        var oldest = now - MaxAge;
        var q = _db.OutboundEmails
            .Where(x => x.Provider == EmailOptions.ProviderRelay && InFlight.Contains(x.Status));
        // On SQL Server the age window is applied in SQL so a backlog of old
        // rows is never loaded; SQLite (tests) can't translate DateTimeOffset
        // comparisons, so there it's applied in memory below.
        if (_db.Database.IsSqlServer()) q = q.Where(x => x.CreatedAt >= oldest);

        var candidates = (await q.ToListAsync(ct))
            .Where(x => x.CreatedAt >= oldest && now - x.CreatedAt >= MinAge)
            .Where(x => x.Status != OutboundEmailStatus.Failed
                // Failed with no relay id never reached the relay; nothing to ask.
                || (x.RelayMessageId is not null && now - x.CreatedAt <= FailedMaxAge))
            .Where(x => x.LastPolledAt is null || now - x.LastPolledAt.Value >=
                (x.Status == OutboundEmailStatus.Failed ? FailedCooldown : Cooldown))
            // Least recently asked first, so a pile of one kind of row can't
            // starve the rest.
            .OrderBy(x => x.LastPolledAt ?? x.CreatedAt)
            .Take(BatchSize)
            .ToList();

        var changed = 0;
        foreach (var row in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await PullAndSaveAsync(row, now, ct) == "applied") changed++;
        }
        if (candidates.Count > 0)
            _log.LogInformation("Email relay reconcile: checked {Checked}, updated {Changed}", candidates.Count, changed);
        return changed;
    }

    public async Task<string> RefreshAsync(Guid outboundEmailId, CancellationToken ct = default)
    {
        var row = await _db.OutboundEmails.FirstOrDefaultAsync(x => x.Id == outboundEmailId, ct);
        if (row is null) return "not-found";
        if (row.Provider != EmailOptions.ProviderRelay) return "not a relay email — SMTP has no later status";
        if (!_client.IsConfigured(out var problem)) return problem!;
        return await PullAndSaveAsync(row, _clock.GetUtcNow(), ct);
    }

    /// <summary>Asks the relay about one row and saves the answer straight
    /// away — per row, not per batch, so a webhook that lands while a long
    /// batch is still pulling isn't overwritten by a stale answer.</summary>
    private async Task<string> PullAndSaveAsync(OutboundEmail row, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(row.RelayMessageId))
        {
            return await OutboundEmailStore.ApplyAndSaveAsync(_db, row, () =>
            {
                row.LastPolledAt = now;
                // A webhook can still match this by idempotency key and move
                // it to Sent — Failed is not terminal.
                return row.Status == OutboundEmailStatus.Pending && now - row.CreatedAt >= PendingTimeout
                    ? RelayStatus.Apply(row, new RelayStatus.Update(OutboundEmailStatus.Failed,
                        Error: "The send was interrupted before the relay answered; outcome unknown."), "pull", now)
                    : "no-relay-id";
            }, ct);
        }

        var (outcome, update) = await PullAsync(row, ct);
        return await OutboundEmailStore.ApplyAndSaveAsync(_db, row, () =>
        {
            row.LastPolledAt = now;
            return update is null ? outcome : RelayStatus.Apply(row, update, "pull", now);
        }, ct);
    }

    /// <summary>The relay's answer for one row: an update to apply, or an
    /// error description.</summary>
    private async Task<(string Outcome, RelayStatus.Update? Update)> PullAsync(OutboundEmail row, CancellationToken ct)
    {

        RelayResponse resp;
        try
        {
            resp = await _client.GetMessageAsync(row.RelayMessageId!, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Email relay status pull failed for {RelayId}", row.RelayMessageId);
            return ($"error: {ex.GetType().Name}: {ex.Message}", null);
        }

        if (!resp.IsSuccessStatus || resp.Data is not { } data)
        {
            // 403 here usually means the API key lacks the "read" scope.
            _log.LogWarning("Email relay status pull for {RelayId}: {Detail}", row.RelayMessageId, resp.Describe());
            return ($"error: {resp.Describe()}", null);
        }

        var update = RelayMessageView.ToUpdate(data);
        if (update is null) return ("error: unrecognised status in relay response", null);
        return ("pulled", update with
        {
            Error = update.Error is null ? null : EmailRelayTransport.Truncate(update.Error, 2000)
        });
    }
}

using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Notifications.Relay;

public interface IEmailRelayReconciler
{
    /// <summary>Pull the current status of relay emails still in flight.</summary>
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
    /// <summary>Don't ask about the same email more than this often.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
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

        // Status/provider filter in SQL; the date filter in memory, because
        // SQLite (the test database) can't translate DateTimeOffset comparisons.
        // In-flight relay rows are a small set, so this costs nothing real.
        var candidates = (await _db.OutboundEmails
                .Where(x => x.Provider == EmailOptions.ProviderRelay && InFlight.Contains(x.Status))
                .ToListAsync(ct))
            .Where(x => now - x.CreatedAt <= MaxAge && now - x.CreatedAt >= MinAge)
            .Where(x => x.LastPolledAt is null || now - x.LastPolledAt.Value >= Cooldown)
            // Failed with no relay id = never reached the relay; nothing to ask.
            .Where(x => x.Status != OutboundEmailStatus.Failed || x.RelayMessageId is not null)
            .OrderBy(x => x.CreatedAt)
            .Take(BatchSize)
            .ToList();

        var changed = 0;
        foreach (var row in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await PullAsync(row, now, ct) == "applied") changed++;
        }
        if (candidates.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Email relay reconcile: checked {Checked}, updated {Changed}", candidates.Count, changed);
        }
        return changed;
    }

    public async Task<string> RefreshAsync(Guid outboundEmailId, CancellationToken ct = default)
    {
        var row = await _db.OutboundEmails.FirstOrDefaultAsync(x => x.Id == outboundEmailId, ct);
        if (row is null) return "not-found";
        if (row.Provider != EmailOptions.ProviderRelay) return "not a relay email — SMTP has no later status";
        if (!_client.IsConfigured(out var problem)) return problem!;
        var outcome = await PullAsync(row, _clock.GetUtcNow(), ct);
        await _db.SaveChangesAsync(ct);
        return outcome;
    }

    /// <summary>Asks the relay about one row and applies the answer. Leaves
    /// saving to the caller.</summary>
    private async Task<string> PullAsync(OutboundEmail row, DateTimeOffset now, CancellationToken ct)
    {
        row.LastPolledAt = now;

        if (string.IsNullOrEmpty(row.RelayMessageId))
        {
            if (row.Status == OutboundEmailStatus.Pending && now - row.CreatedAt >= PendingTimeout)
            {
                // A webhook can still match this by idempotency key and move
                // it to Sent — Failed is not terminal.
                return RelayStatus.Apply(row, new RelayStatus.Update(OutboundEmailStatus.Failed,
                    Error: "The send was interrupted before the relay answered; outcome unknown."), "pull", now);
            }
            return "no-relay-id";
        }

        RelayResponse resp;
        try
        {
            resp = await _client.GetMessageAsync(row.RelayMessageId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Email relay status pull failed for {RelayId}", row.RelayMessageId);
            return $"error: {ex.GetType().Name}: {ex.Message}";
        }

        if (!resp.IsSuccessStatus || resp.Data is not { } data)
        {
            // 403 here usually means the API key lacks the "read" scope.
            _log.LogWarning("Email relay status pull for {RelayId}: {Detail}", row.RelayMessageId, resp.Describe());
            return $"error: {resp.Describe()}";
        }

        var update = RelayMessageView.ToUpdate(data);
        if (update is null) return "error: unrecognised status in relay response";
        return RelayStatus.Apply(row, update with
        {
            Error = update.Error is null ? null : EmailRelayTransport.Truncate(update.Error, 2000)
        }, "pull", now);
    }
}

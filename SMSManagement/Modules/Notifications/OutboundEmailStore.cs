using Microsoft.EntityFrameworkCore;

namespace SMSManagement.Modules.Notifications;

/// <summary>
/// Saves a status change to an <see cref="OutboundEmail"/> without losing a
/// concurrent one. <c>Status</c> is a concurrency token, so the UPDATE carries
/// <c>WHERE Status = &lt;what we read&gt;</c>. If the send path, a webhook and the
/// reconciler race, the loser reloads the row and re-applies its change to
/// the fresh state. That is where <see cref="Relay.RelayStatus.Apply"/>'s
/// "Sent is terminal" rule gets enforced: a stale "queued" lands on a row
/// that is already Sent and is ignored.
/// </summary>
internal static class OutboundEmailStore
{
    private const int MaxAttempts = 3;

    /// <param name="apply">Mutates the tracked row and returns an outcome
    /// label. Called again after each reload, so it must be safe to repeat.</param>
    public static async Task<T> ApplyAndSaveAsync<T>(
        DbContext db, OutboundEmail row, Func<T> apply, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var outcome = apply();
            try
            {
                await db.SaveChangesAsync(ct);
                return outcome;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                await db.Entry(row).ReloadAsync(ct);
            }
        }
    }
}

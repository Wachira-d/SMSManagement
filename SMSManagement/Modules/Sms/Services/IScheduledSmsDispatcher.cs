namespace SMSManagement.Modules.Sms.Services;

/// <summary>
/// Picks up SMS rows whose <c>ScheduledFor</c> is now due and hands each one
/// to <see cref="ISmsDispatcher"/>. Invoked by a Hangfire recurring job
/// every minute.
///
/// Why this is its own service: without it, anything dispatched via the
/// workflow engine or via /api/projects/{id}/sms/send with a future
/// <c>scheduledFor</c> sits in Queued forever — the EnqueueAsync path only
/// persists, never triggers a send. This worker closes that loop.
/// </summary>
public interface IScheduledSmsDispatcher
{
    /// <summary>How many messages to drain per tick. Bounded so a backlog
    /// can't monopolise the worker.</summary>
    int BatchSize { get; }

    Task<int> DispatchDueAsync(CancellationToken ct = default);
}

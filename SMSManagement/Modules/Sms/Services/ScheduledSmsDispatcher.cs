using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Sms.Domain;

namespace SMSManagement.Modules.Sms.Services;

public sealed class ScheduledSmsDispatcher : IScheduledSmsDispatcher
{
    private readonly AppDbContext _db;
    private readonly ISmsDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly ILogger<ScheduledSmsDispatcher> _log;

    public int BatchSize { get; } = 200;

    public ScheduledSmsDispatcher(
        AppDbContext db,
        ISmsDispatcher dispatcher,
        TimeProvider clock,
        ILogger<ScheduledSmsDispatcher> log)
    {
        _db = db;
        _dispatcher = dispatcher;
        _clock = clock;
        _log = log;
    }

    public async Task<int> DispatchDueAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        // Pick rows that are either:
        //  - Immediate priority but Queued (the dispatcher was never called — recovery path)
        //  - Scheduled with ScheduledFor <= now
        //  - Or Batch priority (drained continuously, provider-rate-limit permitting)
        var ids = await _db.SmsMessages
            .Where(m => m.Status == SmsStatus.Queued
                     && (m.ScheduledFor == null || m.ScheduledFor <= now))
            .OrderBy(m => m.ScheduledFor ?? m.CreatedAt)
            .Take(BatchSize)
            .Select(m => m.Id)
            .ToListAsync(ct);

        if (ids.Count == 0) return 0;

        _log.LogInformation("Draining {Count} due SMS messages.", ids.Count);

        var dispatched = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _dispatcher.DispatchAsync(id, ct);
                dispatched++;
            }
            catch (Exception ex)
            {
                // SmsDispatcher already handles provider exceptions / retries internally;
                // a throw here means something unexpected (e.g. encryption envelope corrupted).
                // Log and skip — don't let one bad row kill the whole tick.
                _log.LogError(ex, "Scheduled dispatch of SMS {Id} failed.", id);
            }
        }
        return dispatched;
    }
}

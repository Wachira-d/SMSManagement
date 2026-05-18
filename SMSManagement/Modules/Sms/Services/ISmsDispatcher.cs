using SMSManagement.Modules.Sms.Domain;

namespace SMSManagement.Modules.Sms.Services;

public interface ISmsDispatcher
{
    /// <summary>Persist + enqueue. Idempotent on the dedup key.</summary>
    Task<Guid> EnqueueAsync(SmsRequest request, CancellationToken ct = default);

    /// <summary>Worker entry point — picks up a queued row and dispatches it.</summary>
    Task DispatchAsync(Guid smsMessageId, CancellationToken ct = default);
}

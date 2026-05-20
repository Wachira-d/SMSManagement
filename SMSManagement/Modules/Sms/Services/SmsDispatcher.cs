using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Observability;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Sms.Providers;

namespace SMSManagement.Modules.Sms.Services;

/// <summary>
/// Persists requests, deduplicates, hands off to a provider, and records outcomes.
/// Provider calls are wrapped by Polly in DI — this class only sees the final result/exception.
/// </summary>
public sealed class SmsDispatcher : ISmsDispatcher
{
    private readonly AppDbContext _db;
    private readonly FieldEncryptor _crypto;
    private readonly IProviderRouter _router;
    private readonly CampaignMetrics _metrics;
    private readonly ILogger<SmsDispatcher> _log;
    private readonly TimeProvider _clock;

    private const short MaxAttempts = 5;

    public SmsDispatcher(
        AppDbContext db,
        FieldEncryptor crypto,
        IProviderRouter router,
        CampaignMetrics metrics,
        TimeProvider clock,
        ILogger<SmsDispatcher> log)
    {
        _db = db;
        _crypto = crypto;
        _router = router;
        _metrics = metrics;
        _clock = clock;
        _log = log;
    }

    public async Task<Guid> EnqueueAsync(SmsRequest request, CancellationToken ct = default)
    {
        var dedup = ComputeDedupKey(request);

        // IgnoreQueryFilters: this is an internal idempotency check — it must
        // see every row regardless of the caller's project scope (the engine
        // can enqueue from an anonymous click-driven signal). DedupKey is
        // globally unique so a hit is unambiguous.
        var existing = await _db.SmsMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.DedupKey == dedup, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            _log.LogInformation("Dedup hit project={ProjectId} dedup={DedupKey}",
                request.ProjectId, dedup);
            return existing.Id;
        }

        var msg = new SmsMessage
        {
            ProjectId = request.ProjectId,
            WorkflowInstanceId = request.WorkflowInstanceId,
            DedupKey = dedup,
            Provider = _router.Resolve(request).Name,
            MaskedTo = PiiMasking.MaskPhone(request.Recipient),
            EncryptedTo = _crypto.Encrypt(request.Recipient),
            EncryptedBody = _crypto.Encrypt(request.Body),
            Priority = request.Priority,
            ScheduledFor = request.ScheduledFor,
            Status = SmsStatus.Queued
        };
        _db.SmsMessages.Add(msg);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return msg.Id;
    }

    public async Task DispatchAsync(Guid smsMessageId, CancellationToken ct = default)
    {
        var msg = await _db.SmsMessages.FindAsync([smsMessageId], ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"SMS {smsMessageId} not found");

        if (msg.Status is SmsStatus.Sent or SmsStatus.Delivered)
        {
            _log.LogDebug("SMS {Id} already terminal ({Status}); skipping", msg.Id, msg.Status);
            return;
        }
        if (msg.Attempts >= MaxAttempts)
        {
            msg.Status = SmsStatus.Failed;
            msg.ErrorCode = "MAX_ATTEMPTS";
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        var recipient = _crypto.Decrypt(msg.EncryptedTo);
        var body = _crypto.Decrypt(msg.EncryptedBody);
        var req = new SmsRequest(msg.ProjectId, recipient, body, null,
            msg.Priority, msg.ScheduledFor, msg.WorkflowInstanceId);

        var provider = _router.Resolve(req);
        msg.Provider = provider.Name;

        msg.Attempts++;
        msg.Status = SmsStatus.Sending;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var startedAt = _clock.GetUtcNow();
        try
        {
            var result = await provider.DispatchAsync(req, ct).ConfigureAwait(false);
            if (result.Success)
            {
                msg.Status = SmsStatus.Sent;
                msg.SentAt = _clock.GetUtcNow();
                msg.ProviderMessageId = result.ProviderMessageId;
                msg.ErrorCode = null;
                _metrics.SmsDispatched.Add(1,
                    KeyValuePair.Create<string, object?>("provider", provider.Name), KeyValuePair.Create<string, object?>("result", "sent"));
            }
            else
            {
                msg.Status = SmsStatus.Rejected;
                msg.ErrorCode = result.ErrorCode;
                _metrics.SmsDispatched.Add(1,
                    KeyValuePair.Create<string, object?>("provider", provider.Name), KeyValuePair.Create<string, object?>("result", "rejected"));
                _metrics.SmsFailed.Add(1, KeyValuePair.Create<string, object?>("provider", provider.Name));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Polly already exhausted retries / opened the circuit on the primary.
            // Try the failover chain once before marking the message for re-queue.
            var fallback = _router.Fallback(req, provider.Name);
            if (fallback is not null)
            {
                try
                {
                    var fbResult = await fallback.DispatchAsync(req, ct).ConfigureAwait(false);
                    if (fbResult.Success)
                    {
                        msg.Provider = fallback.Name;
                        msg.Status = SmsStatus.Sent;
                        msg.SentAt = _clock.GetUtcNow();
                        msg.ProviderMessageId = fbResult.ProviderMessageId;
                        msg.ErrorCode = null;
                        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception fbEx) when (fbEx is not OperationCanceledException)
                {
                    _log.LogWarning(fbEx, "SMS failover provider {Fallback} also failed",
                        fallback.Name);
                }
            }

            msg.Status = msg.Attempts >= MaxAttempts ? SmsStatus.Failed : SmsStatus.Queued;
            msg.ErrorCode = ex.GetType().Name;
            _metrics.SmsDispatched.Add(1,
                KeyValuePair.Create<string, object?>("provider", provider.Name), KeyValuePair.Create<string, object?>("result", "exception"));
            if (msg.Status == SmsStatus.Failed)
                _metrics.SmsFailed.Add(1, KeyValuePair.Create<string, object?>("provider", provider.Name));
            _log.LogWarning(ex, "SMS dispatch attempt {Attempt} failed for {Id}", msg.Attempts, msg.Id);
        }
        finally
        {
            _metrics.SmsDispatchLatencyMs.Record(
                (_clock.GetUtcNow() - startedAt).TotalMilliseconds,
                KeyValuePair.Create<string, object?>("provider", provider.Name));
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static string ComputeDedupKey(SmsRequest r)
    {
        var bodyHash = SHA256.HashData(Encoding.UTF8.GetBytes(r.Body));
        // DedupDiscriminator (e.g. workflow "{instanceId}:{step}:{repeat}")
        // makes each intentional resend its own key while a re-run of the
        // exact same logical send still collides. Empty for ad-hoc API sends.
        var raw = $"{r.ProjectId:N}|{r.Recipient}|{Convert.ToHexString(bodyHash)}|{r.DedupDiscriminator}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}

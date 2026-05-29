namespace SMSManagement.Modules.Sms.Domain;

public enum SmsStatus
{
    Queued = 0,
    Sending = 1,
    Sent = 2,
    Delivered = 3,
    Failed = 4,
    Rejected = 5,
    Expired = 6
}

public enum SmsPriority { Immediate = 0, Scheduled = 1, Batch = 2 }

/// <summary>Persistent record of a single outbound SMS.</summary>
public sealed class SmsMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid? WorkflowInstanceId { get; set; }

    /// <summary>SHA-256(projectId | recipient | bodyHash) — unique constraint enforces dedup.</summary>
    public string DedupKey { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;
    public string? ProviderMessageId { get; set; }

    /// <summary>Caller-supplied sender id / CLI for this message. Null means
    /// "use the provider's configured default sender".</summary>
    public string? SenderId { get; set; }

    /// <summary>Human-safe display value (e.g. "+66****1234"). Goes in logs.</summary>
    public string MaskedTo { get; set; } = string.Empty;

    /// <summary>AES-GCM ciphertext of the recipient MSISDN.</summary>
    public byte[] EncryptedTo { get; set; } = Array.Empty<byte>();

    /// <summary>AES-GCM ciphertext of the message body.</summary>
    public byte[] EncryptedBody { get; set; } = Array.Empty<byte>();

    public SmsStatus Status { get; set; } = SmsStatus.Queued;
    public SmsPriority Priority { get; set; } = SmsPriority.Immediate;

    public short Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ScheduledFor { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? ErrorCode { get; set; }

    /// <summary>Last time the delivery-status reconciler pulled this message
    /// from the provider's query API. Used to throttle re-queries (and is
    /// updated even when the status didn't change, so the same row isn't
    /// re-asked on every report download).</summary>
    public DateTimeOffset? LastStatusQueryAt { get; set; }

    /// <summary>When we received any delivery notification — push OR pull —
    /// regardless of the resulting status. Differs from <see cref="DeliveredAt"/>
    /// in that a Failed/Rejected DN also stamps this column, so a timeline
    /// for non-Delivered messages exists.</summary>
    public DateTimeOffset? DnReceivedAt { get; set; }

    /// <summary>Verbatim status word + carrier detail from the provider's last
    /// DN (e.g. <c>DELIVERED: 11:05:23 +0700</c> or <c>UNDELIVERED: phone
    /// off</c>). Informational column for the operator; the machine-readable
    /// failure category lives in <see cref="ErrorCode"/>.</summary>
    public string? StatusDetail { get; set; }

    /// <summary>Which path most recently wrote <see cref="Status"/>:
    /// <c>webhook</c> for the push DN, <c>pull</c> for the reconciler's
    /// status query. Useful for diagnosing DN reliability — a high ratio of
    /// <c>pull</c> means the provider's DN webhook is dropping.</summary>
    public string? StatusSource { get; set; }

    /// <summary>Verbatim DN payload (query string for etracker, the
    /// individual result JSON for Infobip) so an operator can inspect what
    /// the provider actually sent — including timestamp / operator / charge
    /// fields we don't have a typed column for. Capped at 4 KB; truncated
    /// when longer.</summary>
    public string? DnRawPayload { get; set; }

    /// <summary>Verbatim provider response from the last dispatch attempt
    /// (etracker gateway body / Infobip payload). Diagnostic only — surfaced
    /// in the SMS detail view so operators can see why a send was rejected.</summary>
    public string? RawProviderResponse { get; set; }
}

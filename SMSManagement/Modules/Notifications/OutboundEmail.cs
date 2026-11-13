namespace SMSManagement.Modules.Notifications;

/// <summary>
/// Lifecycle of one outbound email. The lower-case wire forms of Queued …
/// Cancelled match EmailRelayAPI's own statuses one-for-one.
/// </summary>
public enum OutboundEmailStatus
{
    /// <summary>Row written, transport not yet called.</summary>
    Pending = 0,
    Queued = 1,
    Scheduled = 2,
    Sending = 3,
    Sent = 4,
    Failed = 5,
    Cancelled = 6,
    /// <summary>The transport refused the message outright (relay 4xx, SMTP
    /// misconfiguration). Distinct from Failed, which is a delivery attempt
    /// that didn't succeed.</summary>
    Rejected = 7,
    /// <summary>No transport is configured, so nothing was attempted.</summary>
    Skipped = 8
}

/// <summary>
/// One row per email the platform sends, whatever the transport. Before this
/// existed an SMTP failure was logged and dropped, and a relay send had
/// nothing local for its delivery webhook to reconcile against.
///
/// Bodies are deliberately not stored — password-reset mail carries a live
/// link — only enough to answer "did it go, and to whom".
/// </summary>
public sealed class OutboundEmail
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary><c>smtp</c> or <c>relay</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Which feature sent it (ingestion-report, sms-round-summary,
    /// coupon-shortage, password-reset, admin-test, …).</summary>
    public string? Category { get; set; }

    public string Recipients { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public int AttachmentCount { get; set; }

    public OutboundEmailStatus Status { get; set; } = OutboundEmailStatus.Pending;

    /// <summary>Which path last wrote <see cref="Status"/>: <c>send</c>,
    /// <c>webhook</c> or <c>pull</c>.</summary>
    public string? StatusSource { get; set; }
    public DateTimeOffset? StatusUpdatedAt { get; set; }

    /// <summary>EmailRelayAPI's message id, from the 202 response.</summary>
    public string? RelayMessageId { get; set; }

    /// <summary>Sent to the relay so a retried send is deduplicated instead of
    /// delivered twice. Also the fallback key for matching a webhook when the
    /// 202 response was lost before RelayMessageId could be stored.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Downstream SMTP message id once the relay has delivered.</summary>
    public string? ProviderMessageId { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Throttles the status reconciler.</summary>
    public DateTimeOffset? LastPolledAt { get; set; }

    public bool IsFinal => Status is OutboundEmailStatus.Sent or OutboundEmailStatus.Failed
        or OutboundEmailStatus.Cancelled or OutboundEmailStatus.Rejected
        or OutboundEmailStatus.Skipped;
}

/// <summary>
/// One row per status webhook the relay posts at us — including the ones we
/// reject — so "did the relay call back, and what did we do with it" has an
/// answer without reading log files. Mirrors DnLog for SMS.
/// </summary>
public sealed class EmailWebhookLog
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary><c>email.sent</c> / <c>email.failed</c>, or null if unparsed.</summary>
    public string? Event { get; set; }
    public string? RelayMessageId { get; set; }
    public Guid? OutboundEmailId { get; set; }

    /// <summary>applied, duplicate, ignored-stale, unknown-message,
    /// bad-signature, missing-signature, stale-timestamp, parse-error, disabled.</summary>
    public string Outcome { get; set; } = string.Empty;
    public string? RemoteIp { get; set; }

    /// <summary>Verbatim body, capped at 4 KB.</summary>
    public string? RawPayload { get; set; }
    public string? Notes { get; set; }
}

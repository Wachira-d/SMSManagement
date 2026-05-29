namespace SMSManagement.Modules.Sms.Webhooks;

/// <summary>
/// Structured per-DN audit row. Every webhook / pull-status interaction
/// writes one of these — successful, rejected, or "unknown message id" — so
/// the operator can investigate exactly what the provider sent, without
/// trawling JSON log files. Complements <c>SmsMessage.DnRawPayload</c>
/// which only captures the LATEST DN per message; this captures EVERY DN.
/// </summary>
public sealed class DnLog
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary><c>etracker</c> or <c>infobip</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary><c>webhook</c> (push) or <c>pull</c> (status query).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Provider-side message id (matches
    /// <c>SmsMessage.ProviderMessageId</c>). Null when the DN couldn't
    /// be parsed enough to identify a message.</summary>
    public string? ProviderMessageId { get; set; }

    /// <summary>Outcome flavour: <c>accepted</c>, <c>rejected</c>,
    /// <c>unknown-message</c>, <c>parse-error</c>, <c>unauthorized</c>.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Raw status word from the provider (DELIVERED / UNDELIVERED /
    /// REJECTED / 200 / 400 / …). Null for rejected requests where we
    /// didn't get that far.</summary>
    public string? Status { get; set; }

    /// <summary>Mapped <see cref="Domain.SmsStatus"/> as a string — the
    /// internal interpretation of <see cref="Status"/>.</summary>
    public string? MappedStatus { get; set; }

    /// <summary>Verbatim provider payload (query string for etracker, the
    /// per-result JSON for Infobip). Capped at 4 KB.</summary>
    public string? RawPayload { get; set; }

    /// <summary>Comma-separated list of field names the provider sent.
    /// Cheap query target when an operator is hunting for "which payload
    /// has the Received field" without scanning RawPayload text.</summary>
    public string? FieldKeys { get; set; }

    /// <summary>Remote IP of the caller — diagnoses provider-side network /
    /// IP-allowlist questions.</summary>
    public string? RemoteIp { get; set; }

    /// <summary>Free-form note (e.g. the reject reason, the unknown-message
    /// id, the exception class). Capped at 500 chars.</summary>
    public string? Notes { get; set; }
}

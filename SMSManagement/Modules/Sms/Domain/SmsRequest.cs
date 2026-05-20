namespace SMSManagement.Modules.Sms.Domain;

/// <summary>Inbound contract — what a caller (workflow / API) hands to the dispatcher.</summary>
public sealed record SmsRequest(
    Guid ProjectId,
    string Recipient,
    string Body,
    string? SenderId,
    SmsPriority Priority,
    DateTimeOffset? ScheduledFor,
    Guid? WorkflowInstanceId,
    IReadOnlyDictionary<string, string>? Metadata = null,
    /// <summary>
    /// Optional discriminator folded into the dedup key. The dedup key exists
    /// to absorb accidental double-submits (a Hangfire job that runs twice,
    /// a double-clicked Send button) — NOT to block intentional resends.
    /// A self-looping reminder step renders the same body every loop, so
    /// without this two legitimate reminders 5 days apart would collide on
    /// (project|recipient|bodyHash) and the second would be silently dropped.
    /// The workflow engine passes "{instanceId}:{step}:{repeatCount}" so each
    /// loop iteration is its own message, while a re-run of the SAME iteration
    /// still dedups. Null for ad-hoc API sends (legacy behaviour preserved).
    /// </summary>
    string? DedupDiscriminator = null);

/// <summary>What an SMS provider returns after attempting dispatch.</summary>
public sealed record ProviderDispatchResult(
    bool Success,
    string? ProviderMessageId,
    string? ErrorCode,
    string? RawResponse);

namespace SMSManagement.Modules.Workflow.Domain;

public enum WorkflowState
{
    Pending = 0,
    Scheduled = 1,
    Dispatching = 2,
    AwaitingAction = 3,
    ReminderDue = 4,
    Completed = 5,
    Failed = 6,
    Expired = 7
}

public sealed class WorkflowInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DefinitionId { get; set; }
    public Guid? IngestionBatchId { get; set; }

    public WorkflowState State { get; set; } = WorkflowState.Pending;
    public string CurrentStep { get; set; } = string.Empty;
    public int StepRepeatCount { get; set; }

    /// <summary>Display-only (masked) recipient — never the raw value.</summary>
    public string MaskedPhone { get; set; } = string.Empty;

    /// <summary>AES-GCM payload row from ingestion (the source CSV row).</summary>
    public byte[] EncryptedPayload { get; set; } = Array.Empty<byte>();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NextCheckAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Coarse processing lease. A tick or signal claims the instance by
    /// setting this to a short future time before touching it; a second pass sees
    /// the live lease and skips, so the same instance is never processed (and
    /// never double-sent) concurrently. A crash leaves a stale lease that simply
    /// expires, so the row is never permanently stuck.</summary>
    public DateTimeOffset? ProcessingLockedUntil { get; set; }
}

public sealed class WorkflowTransition
{
    public long Id { get; set; }
    public Guid InstanceId { get; set; }
    public WorkflowState FromState { get; set; }
    public WorkflowState ToState { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string? DataJson { get; set; }
}

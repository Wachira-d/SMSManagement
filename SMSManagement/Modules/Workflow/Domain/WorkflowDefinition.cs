namespace SMSManagement.Modules.Workflow.Domain;

public sealed class WorkflowDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public bool Active { get; set; } = true;

    /// <summary>JSON document describing the DAG of steps. See <see cref="WorkflowSpec"/>.</summary>
    public string DefinitionJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Declarative DAG. Kept simple and JSON-serialisable.</summary>
public sealed class WorkflowSpec
{
    public string InitialStep { get; set; } = "send";
    public Dictionary<string, WorkflowStep> Steps { get; set; } = new();

    /// <summary>Hard expiry from instance start (e.g. 60 days).</summary>
    public TimeSpan Expiration { get; set; } = TimeSpan.FromDays(60);
}

public sealed class WorkflowStep
{
    /// <summary>"send_sms" | "shortlink" | "wait" | "complete"</summary>
    public string Type { get; set; } = "send_sms";

    /// <summary>Template for body / URL — supports {{column}} substitution.</summary>
    public string? Template { get; set; }

    /// <summary>Wait duration before evaluating the transition.</summary>
    public TimeSpan? Wait { get; set; }

    /// <summary>Max repeats of this step (e.g. up to 3 reminders).</summary>
    public int MaxRepeats { get; set; } = 1;

    /// <summary>Conditional transitions. Key = signal name, value = next step.</summary>
    public Dictionary<string, string> OnSignal { get; set; } = new();

    /// <summary>Fallback transition when wait elapses with no signal.</summary>
    public string? OnTimeout { get; set; }
}

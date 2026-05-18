using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Shortlink.Services;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Sms.Services;
using SMSManagement.Modules.Workflow.Domain;

namespace SMSManagement.Modules.Workflow.Engine;

/// <summary>
/// Durable, restartable orchestrator. Every transition is persisted before the next
/// side-effect, so a crash mid-step results in a retry, never silent loss.
/// </summary>
public sealed class WorkflowEngine : IWorkflowEngine
{
    private readonly AppDbContext _db;
    private readonly FieldEncryptor _crypto;
    private readonly ISmsDispatcher _sms;
    private readonly IShortlinkService _shortlinks;
    private readonly TimeProvider _clock;
    private readonly ILogger<WorkflowEngine> _log;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public WorkflowEngine(
        AppDbContext db,
        FieldEncryptor crypto,
        ISmsDispatcher sms,
        IShortlinkService shortlinks,
        TimeProvider clock,
        ILogger<WorkflowEngine> log)
    {
        _db = db;
        _crypto = crypto;
        _sms = sms;
        _shortlinks = shortlinks;
        _clock = clock;
        _log = log;
    }

    public async Task<Guid> StartAsync(
        Guid definitionId,
        IReadOnlyDictionary<string, string> payload,
        Guid? batchId,
        CancellationToken ct = default)
    {
        var def = await LoadSpecAsync(definitionId, ct);

        var phone = payload.GetValueOrDefault("phone") ?? string.Empty;
        var payloadJson = JsonSerializer.Serialize(payload);

        var instance = new WorkflowInstance
        {
            DefinitionId = definitionId,
            IngestionBatchId = batchId,
            State = WorkflowState.Pending,
            CurrentStep = def.Spec.InitialStep,
            MaskedPhone = PiiMasking.MaskPhone(phone),
            EncryptedPayload = _crypto.Encrypt(payloadJson),
            ExpiresAt = _clock.GetUtcNow().Add(def.Spec.Expiration)
        };
        _db.WorkflowInstances.Add(instance);
        await _db.SaveChangesAsync(ct);

        await ExecuteStepAsync(instance, def.Spec, ct);
        return instance.Id;
    }

    public async Task SignalAsync(Guid instanceId, string signal, CancellationToken ct = default)
    {
        var instance = await _db.WorkflowInstances.FindAsync([instanceId], ct);
        if (instance is null)
        {
            _log.LogWarning("Signal {Signal} for unknown instance {Id}", signal, instanceId);
            return;
        }
        if (instance.State is WorkflowState.Completed or WorkflowState.Expired or WorkflowState.Failed)
            return;

        var def = await LoadSpecAsync(instance.DefinitionId, ct);
        if (!def.Spec.Steps.TryGetValue(instance.CurrentStep, out var step)) return;

        if (!step.OnSignal.TryGetValue(signal, out var next))
        {
            _log.LogDebug("Signal {Signal} not handled by step {Step}", signal, instance.CurrentStep);
            return;
        }

        await TransitionAsync(instance, next, signal, ct);
        await ExecuteStepAsync(instance, def.Spec, ct);
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        // 1. Hard-expire any instance past its deadline.
        var expired = await _db.WorkflowInstances
            .Where(i => i.ExpiresAt <= now
                        && i.State != WorkflowState.Completed
                        && i.State != WorkflowState.Expired
                        && i.State != WorkflowState.Failed)
            .ToListAsync(ct);
        foreach (var inst in expired)
            await TransitionAsync(inst, "__expired__", "expiration", ct, WorkflowState.Expired);

        // 2. Fire timeouts for AwaitingAction / ReminderDue instances whose wait elapsed.
        var due = await _db.WorkflowInstances
            .Where(i => i.NextCheckAt != null && i.NextCheckAt <= now
                        && (i.State == WorkflowState.AwaitingAction || i.State == WorkflowState.ReminderDue))
            .Take(500)
            .ToListAsync(ct);

        foreach (var inst in due)
        {
            var def = await LoadSpecAsync(inst.DefinitionId, ct);
            if (!def.Spec.Steps.TryGetValue(inst.CurrentStep, out var step) || step.OnTimeout is null)
                continue;

            if (inst.StepRepeatCount + 1 >= step.MaxRepeats && step.OnTimeout == inst.CurrentStep)
            {
                // Reminders exhausted — let expiry handle final state.
                inst.NextCheckAt = null;
                await _db.SaveChangesAsync(ct);
                continue;
            }

            await TransitionAsync(inst, step.OnTimeout, "timeout", ct);
            await ExecuteStepAsync(inst, def.Spec, ct);
        }
    }

    // ---------------- internal ----------------

    private async Task ExecuteStepAsync(WorkflowInstance instance, WorkflowSpec spec, CancellationToken ct)
    {
        if (!spec.Steps.TryGetValue(instance.CurrentStep, out var step)) return;

        var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(
            _crypto.Decrypt(instance.EncryptedPayload), JsonOpts) ?? new();

        switch (step.Type)
        {
            case "send_sms":
            {
                var body = Render(step.Template ?? "{{message}}", payload);

                // Auto-substitute long URLs with shortlinks
                body = await ReplaceUrlsWithShortlinksAsync(body, instance, ct);

                await _sms.EnqueueAsync(new SmsRequest(
                    ProjectId: await ProjectIdForAsync(instance.DefinitionId, ct),
                    Recipient: payload.GetValueOrDefault("phone") ?? string.Empty,
                    Body: body,
                    SenderId: null,
                    Priority: SmsPriority.Immediate,
                    ScheduledFor: null,
                    WorkflowInstanceId: instance.Id), ct);

                instance.State = WorkflowState.AwaitingAction;
                instance.NextCheckAt = step.Wait is { } w ? _clock.GetUtcNow().Add(w) : null;
                instance.StepRepeatCount++;
                await _db.SaveChangesAsync(ct);
                break;
            }
            case "wait":
                instance.State = WorkflowState.AwaitingAction;
                instance.NextCheckAt = step.Wait is { } w ? _clock.GetUtcNow().Add(w) : null;
                await _db.SaveChangesAsync(ct);
                break;

            case "complete":
                instance.State = WorkflowState.Completed;
                instance.NextCheckAt = null;
                await _db.SaveChangesAsync(ct);
                break;
        }
    }

    private async Task<string> ReplaceUrlsWithShortlinksAsync(
        string body, WorkflowInstance instance, CancellationToken ct)
    {
        // Cheap URL detection; production should use a vetted Regex with timeout.
        var tokens = body.Split(' ');
        for (var i = 0; i < tokens.Length; i++)
        {
            if (Uri.TryCreate(tokens[i], UriKind.Absolute, out var uri)
                && (uri.Scheme == "http" || uri.Scheme == "https")
                && tokens[i].Length > 32)
            {
                var slug = await _shortlinks.CreateAsync(
                    await ProjectIdForAsync(instance.DefinitionId, ct),
                    tokens[i], instance.Id, TimeSpan.FromDays(60), null, ct);
                tokens[i] = $"https://s.example/{slug}"; // base from config in real impl
            }
        }
        return string.Join(' ', tokens);
    }

    private async Task TransitionAsync(
        WorkflowInstance instance, string nextStep, string trigger,
        CancellationToken ct, WorkflowState? forceState = null)
    {
        var from = instance.State;
        var to = forceState ?? (nextStep == "__expired__" ? WorkflowState.Expired : WorkflowState.Dispatching);

        instance.State = to;
        instance.CurrentStep = nextStep == "__expired__" ? instance.CurrentStep : nextStep;
        instance.StepRepeatCount = nextStep == instance.CurrentStep ? instance.StepRepeatCount : 0;

        _db.WorkflowTransitions.Add(new WorkflowTransition
        {
            InstanceId = instance.Id,
            FromState = from,
            ToState = to,
            Trigger = trigger,
            At = _clock.GetUtcNow()
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task<(WorkflowDefinition Def, WorkflowSpec Spec)> LoadSpecAsync(
        Guid id, CancellationToken ct)
    {
        var def = await _db.WorkflowDefinitions.FindAsync([id], ct)
                  ?? throw new InvalidOperationException($"Workflow def {id} not found");
        var spec = JsonSerializer.Deserialize<WorkflowSpec>(def.DefinitionJson, JsonOpts)
                   ?? throw new InvalidOperationException("Invalid workflow JSON");
        return (def, spec);
    }

    private async Task<Guid> ProjectIdForAsync(Guid definitionId, CancellationToken ct) =>
        await _db.WorkflowDefinitions
            .Where(d => d.Id == definitionId)
            .Select(d => d.ProjectId)
            .FirstAsync(ct);

    private static string Render(string template, IReadOnlyDictionary<string, string> data)
    {
        var s = template;
        foreach (var kv in data)
            s = s.Replace("{{" + kv.Key + "}}", kv.Value, StringComparison.OrdinalIgnoreCase);
        return s;
    }
}

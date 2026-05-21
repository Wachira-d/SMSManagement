using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Observability;
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
    private readonly ShortlinkOptions _shortlinkOpts;
    private readonly CampaignMetrics _metrics;
    private readonly TimeProvider _clock;
    private readonly ILogger<WorkflowEngine> _log;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Matches any leftover {{placeholder}} a template render didn't fill.</summary>
    private static readonly Regex UnresolvedPlaceholder =
        new(@"\{\{[^{}]*\}\}", RegexOptions.Compiled);

    /// <summary>How long a processing lease is held. Long enough to cover the
    /// slowest step (an SMS enqueue), short enough that a crashed worker's lease
    /// frees the instance within a few ticks.</summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    /// <summary>Recursion budget for pass-through (issue_coupon) step chains —
    /// a misconfigured cycle is caught here instead of overflowing the stack.</summary>
    private const int MaxStepDepth = 50;

    private readonly Modules.Core.Notifications.IUserNotifier _notify;
    private readonly Modules.Coupon.Services.ICouponAllocator _coupons;

    public WorkflowEngine(
        AppDbContext db,
        FieldEncryptor crypto,
        ISmsDispatcher sms,
        IShortlinkService shortlinks,
        IOptionsSnapshot<ShortlinkOptions> shortlinkOpts,
        CampaignMetrics metrics,
        TimeProvider clock,
        ILogger<WorkflowEngine> log,
        Modules.Core.Notifications.IUserNotifier notify,
        Modules.Coupon.Services.ICouponAllocator coupons)
    {
        _db = db;
        _crypto = crypto;
        _sms = sms;
        _shortlinks = shortlinks;
        _shortlinkOpts = shortlinkOpts.Value;
        _metrics = metrics;
        _clock = clock;
        _log = log;
        _notify = notify;
        _coupons = coupons;
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
        if (IsTerminal(instance.State)) return;

        // Claim the lease before touching the instance — if a tick (or another
        // signal) is mid-processing, skip rather than double-advance. The
        // timeout path is the backstop for a signal dropped under contention.
        if (!await TryClaimAsync(instanceId, ct))
        {
            _log.LogDebug("Signal {Signal}: instance {Id} is busy — skipped.", signal, instanceId);
            return;
        }
        try
        {
            // Re-read: a concurrent pass may have advanced the step between the
            // initial Find and the moment the lease was won.
            await _db.Entry(instance).ReloadAsync(ct);
            if (IsTerminal(instance.State)) return;

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
        catch (Exception ex)
        {
            _log.LogError(ex, "Signal {Signal} processing failed for instance {Id}", signal, instanceId);
        }
        finally
        {
            await ReleaseAsync(instanceId, ct);
        }
    }

    private static bool IsTerminal(WorkflowState s) =>
        s is WorkflowState.Completed or WorkflowState.Expired or WorkflowState.Failed;

    /// <summary>Atomically take the processing lease. Returns false when another
    /// pass holds a live lease.</summary>
    private async Task<bool> TryClaimAsync(Guid instanceId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var until = now.Add(LeaseDuration);
        var claimed = await _db.WorkflowInstances
            .Where(i => i.Id == instanceId
                        && (i.ProcessingLockedUntil == null || i.ProcessingLockedUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ProcessingLockedUntil, until), ct);
        return claimed == 1;
    }

    private async Task ReleaseAsync(Guid instanceId, CancellationToken ct)
    {
        try
        {
            await _db.WorkflowInstances
                .Where(i => i.Id == instanceId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    i => i.ProcessingLockedUntil, (DateTimeOffset?)null), ct);
        }
        catch (Exception ex)
        {
            // A stale lease just expires on its own — never let release failure
            // mask the real outcome of the step.
            _log.LogWarning(ex, "Failed to release workflow lease for {Id}", instanceId);
        }
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

        // Only the instances THIS tick actually expired (a concurrent signal
        // may have claimed some) feed the notification roll-up.
        var expiredHere = new List<WorkflowInstance>();
        foreach (var inst in expired)
        {
            if (!await TryClaimAsync(inst.Id, ct)) continue;
            try
            {
                await _db.Entry(inst).ReloadAsync(ct);
                if (IsTerminal(inst.State)) continue;
                await TransitionAsync(inst, "__expired__", "expiration", ct, WorkflowState.Expired);
                expiredHere.Add(inst);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Workflow expire failed for instance {Id}", inst.Id);
            }
            finally
            {
                await ReleaseAsync(inst.Id, ct);
            }
        }

        // Notify project members in bulk so the Workflows tab badge update
        // shows up live. One toast per project (not per instance) so a tick
        // that expires 1000 rows doesn't fire 1000 toasts.
        if (expiredHere.Count > 0)
        {
            var byProject = await _db.WorkflowDefinitions
                .Where(d => expiredHere.Select(e => e.DefinitionId).Contains(d.Id))
                .Select(d => new { d.Id, d.ProjectId })
                .ToListAsync(ct);
            var projMap = byProject.ToDictionary(x => x.Id, x => x.ProjectId);
            var perProject = expiredHere
                .GroupBy(e => projMap.GetValueOrDefault(e.DefinitionId))
                .Where(g => g.Key != Guid.Empty);
            foreach (var g in perProject)
            {
                await _notify.ToProjectAsync(g.Key, new Modules.Core.Notifications.NotificationPayload(
                    Kind: "workflow.instances.expired",
                    Title: "Workflow expired",
                    Body: $"{g.Count()} instance(s) hit their expiration and stopped sending.",
                    Variant: "warning"), ct);
            }
        }

        // 2. Fire timeouts for AwaitingAction / ReminderDue instances whose wait elapsed.
        var due = await _db.WorkflowInstances
            .Where(i => i.NextCheckAt != null && i.NextCheckAt <= now
                        && (i.State == WorkflowState.AwaitingAction || i.State == WorkflowState.ReminderDue))
            .Take(500)
            .ToListAsync(ct);

        foreach (var inst in due)
        {
            if (!await TryClaimAsync(inst.Id, ct)) continue;
            try
            {
                // Re-read under the lease: a signal may have advanced this
                // instance between the query above and the lease being won.
                await _db.Entry(inst).ReloadAsync(ct);
                if (inst.NextCheckAt is null || inst.NextCheckAt > now) continue;
                if (inst.State is not (WorkflowState.AwaitingAction or WorkflowState.ReminderDue))
                    continue;

                var def = await LoadSpecAsync(inst.DefinitionId, ct);
                if (!def.Spec.Steps.TryGetValue(inst.CurrentStep, out var step) || step.OnTimeout is null)
                    continue;

                // StepRepeatCount = how many times this step has executed (the
                // initial run + each timeout re-run). It reaches MaxRepeats after
                // exactly MaxRepeats executions, so the guard is a plain >= with
                // no +1 — the previous "+1 >=" stopped one send early. Only
                // applies to a self-loop (OnTimeout == current step); a timeout
                // pointing at a DIFFERENT step is a one-way transition and the
                // target step enforces its own MaxRepeats.
                if (inst.StepRepeatCount >= step.MaxRepeats && step.OnTimeout == inst.CurrentStep)
                {
                    // Reminders exhausted — let expiry handle final state.
                    inst.NextCheckAt = null;
                    await _db.SaveChangesAsync(ct);
                    continue;
                }

                await TransitionAsync(inst, step.OnTimeout, "timeout", ct);
                await ExecuteStepAsync(inst, def.Spec, ct);
            }
            catch (Exception ex)
            {
                // Isolate failures — one bad instance must not abort the batch.
                _log.LogError(ex, "Workflow tick failed for instance {Id}", inst.Id);
            }
            finally
            {
                await ReleaseAsync(inst.Id, ct);
            }
        }
    }

    // ---------------- internal ----------------

    private async Task ExecuteStepAsync(
        WorkflowInstance instance, WorkflowSpec spec, CancellationToken ct, int depth = 0)
    {
        if (depth > MaxStepDepth)
        {
            // A pass-through (issue_coupon) chain that loops back on itself —
            // fail loud instead of overflowing the stack.
            _log.LogError(
                "Workflow instance {Id} exceeded step depth {Depth} — failing (cyclic spec?).",
                instance.Id, MaxStepDepth);
            await FailAsync(instance, ct);
            return;
        }

        if (!spec.Steps.TryGetValue(instance.CurrentStep, out var step))
        {
            _log.LogError("Workflow instance {Id}: step '{Step}' not found in spec — failing.",
                instance.Id, instance.CurrentStep);
            await FailAsync(instance, ct);
            return;
        }

        var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(
            _crypto.Decrypt(instance.EncryptedPayload), JsonOpts) ?? new();

        switch (step.Type)
        {
            case "send_sms":
            {
                // Inject per-attempt placeholders that aren't part of the row data.
                //   {{attempt}}      — 1-based count for THIS step (1 on first send,
                //                      2 on the first retry, etc.)
                //   {{step}}         — current step name (drip flows use this for
                //                      "Reminder {{attempt}} of {{step}}")
                //   {{step_repeats}} — server-side StepRepeatCount (0-based)
                // These never clash with row columns because the column mapper
                // lowercases everything and these keys are namespaced enough.
                var contextual = new Dictionary<string, string>(payload, StringComparer.OrdinalIgnoreCase)
                {
                    ["attempt"]       = (instance.StepRepeatCount + 1).ToString(),
                    ["step"]          = instance.CurrentStep,
                    ["step_repeats"]  = instance.StepRepeatCount.ToString(),
                };
                var body = Render(step.Template ?? "{{message}}", contextual);

                // Auto-substitute long URLs with shortlinks
                body = await ReplaceUrlsWithShortlinksAsync(body, instance, ct);

                await _sms.EnqueueAsync(new SmsRequest(
                    ProjectId: await ProjectIdForAsync(instance.DefinitionId, ct),
                    Recipient: payload.GetValueOrDefault("phone") ?? string.Empty,
                    Body: body,
                    SenderId: null,
                    Priority: SmsPriority.Immediate,
                    ScheduledFor: null,
                    WorkflowInstanceId: instance.Id,
                    // Discriminator = instance + step + loop iteration. A reminder
                    // self-loop renders the same body each round; without this the
                    // 2nd+ reminder would collide on the dedup key and be dropped.
                    // A re-run of the SAME iteration (engine ticked twice) still
                    // dedups correctly.
                    DedupDiscriminator:
                        $"{instance.Id:N}:{instance.CurrentStep}:{instance.StepRepeatCount}"), ct);

                instance.State = WorkflowState.AwaitingAction;
                instance.NextCheckAt = step.Wait is { } sendWait
                    ? ScheduleAnchor(instance.NextCheckAt).Add(sendWait)
                    : null;
                instance.StepRepeatCount++;
                await _db.SaveChangesAsync(ct);
                break;
            }
            case "issue_coupon":
            {
                // Reserve a coupon for this recipient and inject
                // {{coupon_code}} / {{coupon_url}} into the payload so any
                // later send_sms step can render them. Pass-through: no wait,
                // immediately transition to OnTimeout (the next step).
                if (step.CouponBatchId is { } batchId)
                {
                    var alloc = await _coupons.AllocateAsync(batchId, instance.Id, ct);
                    if (alloc is not null)
                    {
                        payload["coupon_code"] = alloc.Token;
                        payload["coupon_url"]  = alloc.RedeemUrl;
                        instance.EncryptedPayload = _crypto.Encrypt(
                            JsonSerializer.Serialize(payload, JsonOpts));
                        await _db.SaveChangesAsync(ct);
                    }
                    else
                    {
                        // Batch exhausted — log and continue; the SMS just
                        // won't carry a coupon rather than the whole instance
                        // failing.
                        _log.LogWarning(
                            "issue_coupon: batch {Batch} exhausted for instance {Instance}.",
                            batchId, instance.Id);
                    }
                }

                if (step.OnTimeout is { } nextStep && spec.Steps.ContainsKey(nextStep))
                {
                    await TransitionAsync(instance, nextStep, "coupon_issued", ct);
                    await ExecuteStepAsync(instance, spec, ct, depth + 1);
                }
                else
                {
                    // No next step wired — treat as terminal so the instance
                    // doesn't hang in Dispatching forever.
                    instance.State = WorkflowState.Completed;
                    instance.NextCheckAt = null;
                    await _db.SaveChangesAsync(ct);
                }
                break;
            }
            case "wait":
                instance.State = WorkflowState.AwaitingAction;
                instance.NextCheckAt = step.Wait is { } waitDur
                    ? ScheduleAnchor(instance.NextCheckAt).Add(waitDur)
                    : null;
                // Count this execution too — without it a self-looping wait
                // step (OnTimeout = itself) never advances StepRepeatCount and
                // the MaxRepeats guard in TickAsync can never terminate it.
                instance.StepRepeatCount++;
                await _db.SaveChangesAsync(ct);
                break;

            case "complete":
                instance.State = WorkflowState.Completed;
                instance.NextCheckAt = null;
                await _db.SaveChangesAsync(ct);
                break;

            default:
                // Unknown step type — fail rather than silently stranding the
                // instance in Dispatching, invisible to the tick forever.
                _log.LogError("Workflow instance {Id}: unknown step type '{Type}' — failing.",
                    instance.Id, step.Type);
                await FailAsync(instance, ct);
                break;
        }
    }

    /// <summary>Anchor the next wait to the time the current timer was DUE (so a
    /// drip cadence never drifts and a missed tick catches up). A timer still in
    /// the future means a signal superseded the step — anchor to now instead.</summary>
    private DateTimeOffset ScheduleAnchor(DateTimeOffset? pendingCheck)
    {
        var now = _clock.GetUtcNow();
        return pendingCheck is { } p && p <= now ? p : now;
    }

    private async Task FailAsync(WorkflowInstance instance, CancellationToken ct)
    {
        instance.State = WorkflowState.Failed;
        instance.NextCheckAt = null;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<string> ReplaceUrlsWithShortlinksAsync(
        string body, WorkflowInstance instance, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_shortlinkOpts.PublicBaseUrl))
        {
            _log.LogWarning(
                "Shortlink:PublicBaseUrl not configured — leaving URLs as-is.");
            return body;
        }

        // Project-level toggle: when ShortlinkEnabled = false, leave URLs as
        // their original form (operator opted out — e.g. they're tracking
        // clicks via a third-party redirector and don't want a double-hop).
        // IgnoreQueryFilters: the engine runs on behalf of the system, and
        // ExecuteStepAsync can be driven by an anonymous shortlink click
        // (SignalAsync) which has no project membership — the project-scope
        // filter would otherwise hide the project and wrongly skip shortening.
        var projectId = await ProjectIdForAsync(instance.DefinitionId, ct);
        var shortlinkOn = await _db.Projects
            .IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => p.ShortlinkEnabled)
            .FirstOrDefaultAsync(ct);
        if (!shortlinkOn) return body;

        var baseUrl = _shortlinkOpts.PublicBaseUrl.TrimEnd('/');

        // Shorten EVERY http/https URL in the body — not just long ones. A
        // shortlink isn't only about saving characters: it carries the
        // WorkflowInstanceId so a click raises the "shortlink.clicked" signal.
        // Skip a URL only when it is already one of our shortlinks.
        var matches = UrlPatternCache.Matches(body);
        if (matches.Count == 0) return body;

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var shortened = 0;
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var token = m.Value;
            if (replacements.ContainsKey(token)) continue;

            // Peel trailing sentence punctuation off the URL itself.
            var trail = string.Empty;
            var url = token;
            while (url.Length > 0 && ".,;:!?)]}>\"'".IndexOf(url[^1]) >= 0)
            {
                trail = url[^1] + trail;
                url = url[..^1];
            }

            if (url.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                replacements[token] = token;   // already a shortlink, or not a real URL
                continue;
            }

            var slug = await _shortlinks.CreateAsync(
                projectId, url, instance.Id, TimeSpan.FromDays(60), null, ct);
            replacements[token] = $"{baseUrl}/{slug}{trail}";
            shortened++;
        }

        _log.LogInformation(
            "Shortlink: workflow instance {InstanceId} — {Shortened} URL(s) shortened of "
            + "{Found} found in the SMS body.", instance.Id, shortened, matches.Count);

        return UrlPatternCache.Replace(body,
            m => replacements.TryGetValue(m.Value, out var r) ? r : m.Value);
    }

    private static readonly System.Text.RegularExpressions.Regex UrlPatternCache =
        new(@"https?://\S+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(200));

    private async Task TransitionAsync(
        WorkflowInstance instance, string nextStep, string trigger,
        CancellationToken ct, WorkflowState? forceState = null)
    {
        var from = instance.State;
        var to = forceState ?? (nextStep == "__expired__" ? WorkflowState.Expired : WorkflowState.Dispatching);

        // Compute step-change BEFORE mutating CurrentStep. The previous code
        // assigned CurrentStep first then compared nextStep against it — which
        // was always equal, so StepRepeatCount never reset on a real step
        // change. That broke multi-step drip chains: stage2 inherited stage1's
        // repeat count and was treated as already exhausted.
        var isExpiry = nextStep == "__expired__";
        var stepChanged = !isExpiry && nextStep != instance.CurrentStep;

        instance.State = to;
        if (!isExpiry) instance.CurrentStep = nextStep;
        instance.StepRepeatCount = stepChanged ? 0 : instance.StepRepeatCount;

        _db.WorkflowTransitions.Add(new WorkflowTransition
        {
            InstanceId = instance.Id,
            FromState = from,
            ToState = to,
            Trigger = trigger,
            At = _clock.GetUtcNow()
        });
        _metrics.WorkflowTransitions.Add(1,
            KeyValuePair.Create<string, object?>("from", from.ToString()),
            KeyValuePair.Create<string, object?>("to", to.ToString()),
            KeyValuePair.Create<string, object?>("trigger", trigger));
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
        // Strip any placeholder left unresolved — e.g. {{coupon_url}} when a
        // coupon batch was exhausted. Sending the customer a literal "{{...}}"
        // is worse than sending the message without it.
        s = UnresolvedPlaceholder.Replace(s, string.Empty);
        return s;
    }
}

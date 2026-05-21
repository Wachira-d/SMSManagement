using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Identity.Domain;
using SMSManagement.Modules.Identity.Services;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Workflow.Domain;

namespace SMSManagement.Modules.Ingestion.Controllers;

/// <summary>
/// Recent ingestion batches per project — needed for the Sources tab
/// (currently lists the bindings but not the runs against them).
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/ingestion-batches")]
public sealed class IngestionBatchesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IProjectAccessService _access;
    private readonly FieldEncryptor _crypto;

    public IngestionBatchesController(
        AppDbContext db, IProjectAccessService access, FieldEncryptor crypto)
    {
        _db = db;
        _access = access;
        _crypto = crypto;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);

        var rows = await _db.IngestionBatches
            .AsNoTracking()
            .Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.IngestedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(b => new
            {
                b.Id,
                b.SourceType,
                b.SourceRef,
                b.TotalRows,
                b.AcceptedRows,
                b.RejectedRows,
                b.Status,
                b.IngestedAt,
                HasRejections = b.RejectionsJson != null
            })
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>
    /// Per-run ("flow round") rollup: for each ingestion batch, the
    /// stage-by-stage outcome — ingestion (rows), workflow (instances), and
    /// SMS (messages) counts — so an operator can see how many rounds the
    /// flow has run and how each part fared.
    /// </summary>
    [HttpGet("runs")]
    public async Task<IActionResult> Runs(
        Guid projectId, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        take = Math.Clamp(take, 1, 100);

        var batches = await _db.IngestionBatches
            .AsNoTracking()
            .Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.IngestedAt)
            .Take(take)
            .Select(b => new
            {
                b.Id, b.SourceType, b.SourceRef, b.Status, b.IngestedAt,
                b.TotalRows, b.AcceptedRows, b.RejectedRows
            })
            .ToListAsync(ct);

        var batchIds = batches.Select(b => b.Id).ToList();

        // Workflow rollup per batch.
        var wfRollup = await _db.WorkflowInstances
            .AsNoTracking()
            .Where(w => w.IngestionBatchId != null && batchIds.Contains(w.IngestionBatchId.Value))
            .GroupBy(w => w.IngestionBatchId!.Value)
            .Select(g => new
            {
                BatchId   = g.Key,
                Total     = g.Count(),
                Completed = g.Count(x => x.State == WorkflowState.Completed),
                Failed    = g.Count(x => x.State == WorkflowState.Failed
                                      || x.State == WorkflowState.Expired),
                Active    = g.Count(x => x.State != WorkflowState.Completed
                                      && x.State != WorkflowState.Failed
                                      && x.State != WorkflowState.Expired)
            })
            .ToListAsync(ct);

        // SMS rollup per batch (SmsMessage -> WorkflowInstance -> IngestionBatchId).
        var smsRollup = await (
            from m in _db.SmsMessages.AsNoTracking()
            join w in _db.WorkflowInstances.AsNoTracking() on m.WorkflowInstanceId equals w.Id
            where w.IngestionBatchId != null && batchIds.Contains(w.IngestionBatchId.Value)
            group m by w.IngestionBatchId!.Value into g
            select new
            {
                BatchId   = g.Key,
                Total     = g.Count(),
                Delivered = g.Count(x => x.Status == SmsStatus.Delivered),
                Sent      = g.Count(x => x.Status == SmsStatus.Sent),
                Failed    = g.Count(x => x.Status == SmsStatus.Failed
                                      || x.Status == SmsStatus.Rejected
                                      || x.Status == SmsStatus.Expired),
                Pending   = g.Count(x => x.Status == SmsStatus.Queued
                                      || x.Status == SmsStatus.Sending)
            }).ToListAsync(ct);

        var wfMap  = wfRollup.ToDictionary(x => x.BatchId);
        var smsMap = smsRollup.ToDictionary(x => x.BatchId);

        var runs = batches.Select(b => new
        {
            b.Id, b.SourceType, b.SourceRef, b.Status, b.IngestedAt,
            Ingestion = new { b.TotalRows, b.AcceptedRows, b.RejectedRows },
            Workflow = wfMap.TryGetValue(b.Id, out var w)
                ? new { w.Total, w.Completed, w.Failed, w.Active }
                : new { Total = 0, Completed = 0, Failed = 0, Active = 0 },
            Sms = smsMap.TryGetValue(b.Id, out var s)
                ? new { s.Total, s.Delivered, s.Sent, s.Failed, s.Pending }
                : new { Total = 0, Delivered = 0, Sent = 0, Failed = 0, Pending = 0 }
        });

        return Ok(new { count = batches.Count, runs });
    }

    /// <summary>
    /// Full drill-down for one ingestion batch ("flow round"): the ingestion
    /// stage, every workflow instance it spawned with its state-transition
    /// timeline, and every SMS produced — body decrypted, status, and the
    /// verbatim provider response. Lets an operator click a run in the
    /// Pipeline tab and trace exactly what each stage did, per recipient.
    /// </summary>
    [HttpGet("{batchId:guid}/detail")]
    public async Task<IActionResult> Detail(
        Guid projectId, Guid batchId,
        [FromQuery] int take = 500, CancellationToken ct = default)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        take = Math.Clamp(take, 1, 1000);

        var batch = await _db.IngestionBatches
            .AsNoTracking()
            .Where(b => b.Id == batchId && b.ProjectId == projectId)
            .Select(b => new
            {
                b.Id, b.SourceType, b.SourceRef, b.Status, b.IngestedAt,
                b.TotalRows, b.AcceptedRows, b.RejectedRows,
                HasRejections = b.RejectionsJson != null
            })
            .FirstOrDefaultAsync(ct);
        if (batch is null) return NotFound();

        var instances = await _db.WorkflowInstances
            .AsNoTracking()
            .Where(w => w.IngestionBatchId == batchId)
            .OrderBy(w => w.CreatedAt)
            .Take(take)
            .Select(w => new
            {
                w.Id, w.State, w.CurrentStep, w.StepRepeatCount,
                w.MaskedPhone, w.CreatedAt, w.NextCheckAt, w.ExpiresAt
            })
            .ToListAsync(ct);

        var instanceIds = instances.Select(i => i.Id).ToList();

        var transitions = await _db.WorkflowTransitions
            .AsNoTracking()
            .Where(t => instanceIds.Contains(t.InstanceId))
            .OrderBy(t => t.At).ThenBy(t => t.Id)
            .Select(t => new
            {
                t.InstanceId, t.FromState, t.ToState, t.Trigger, t.At, t.DataJson
            })
            .ToListAsync(ct);

        var smsRows = await _db.SmsMessages
            .AsNoTracking()
            .Where(m => m.WorkflowInstanceId != null
                     && instanceIds.Contains(m.WorkflowInstanceId.Value))
            .OrderBy(m => m.CreatedAt)
            .Select(m => new
            {
                m.Id, m.WorkflowInstanceId, m.Provider, m.SenderId, m.MaskedTo,
                m.Status, m.Attempts, m.CreatedAt, m.ScheduledFor, m.SentAt,
                m.DeliveredAt, m.ErrorCode, m.ProviderMessageId,
                m.RawProviderResponse, m.EncryptedBody
            })
            .ToListAsync(ct);

        var txByInstance  = transitions.ToLookup(t => t.InstanceId);
        var smsByInstance = smsRows.ToLookup(m => m.WorkflowInstanceId!.Value);

        string Body(byte[] enc)
        {
            if (enc is null || enc.Length == 0) return "";
            try { return _crypto.Decrypt(enc); }
            catch { return "(decrypt failed)"; }
        }

        var detail = instances.Select(i => new
        {
            i.Id,
            State = i.State.ToString(),
            i.CurrentStep,
            i.StepRepeatCount,
            i.MaskedPhone,
            i.CreatedAt,
            i.NextCheckAt,
            i.ExpiresAt,
            Transitions = txByInstance[i.Id].Select(t => new
            {
                From = t.FromState.ToString(),
                To = t.ToState.ToString(),
                t.Trigger, t.At, t.DataJson
            }),
            Sms = smsByInstance[i.Id].Select(m => new
            {
                m.Id, m.Provider, m.SenderId, m.MaskedTo,
                Status = m.Status.ToString(),
                m.Attempts, m.CreatedAt, m.ScheduledFor, m.SentAt,
                m.DeliveredAt, m.ErrorCode, m.ProviderMessageId,
                m.RawProviderResponse,
                Body = Body(m.EncryptedBody)
            })
        });

        return Ok(new
        {
            batch.Id, batch.SourceType, batch.SourceRef, batch.Status, batch.IngestedAt,
            Ingestion = new
            {
                batch.TotalRows, batch.AcceptedRows, batch.RejectedRows,
                batch.HasRejections
            },
            InstanceCount = instances.Count,
            Truncated = instances.Count == take,
            Instances = detail
        });
    }

    /// <summary>Returns the captured rejection sample (first 50) for one batch.</summary>
    [HttpGet("{batchId:guid}/rejections")]
    public async Task<IActionResult> Rejections(
        Guid projectId, Guid batchId, CancellationToken ct)
    {
        await _access.EnsureAsync(projectId, ProjectAccessLevel.Viewer, ct);
        var json = await _db.IngestionBatches
            .AsNoTracking()
            .Where(b => b.Id == batchId && b.ProjectId == projectId)
            .Select(b => b.RejectionsJson)
            .FirstOrDefaultAsync(ct);
        if (json is null) return Ok(new { items = Array.Empty<object>(), total = 0 });
        // Already JSON — bounce it through Content-Type:application/json directly
        // so we don't deserialize-then-reserialize the bounded sample.
        return Content(json, "application/json");
    }
}

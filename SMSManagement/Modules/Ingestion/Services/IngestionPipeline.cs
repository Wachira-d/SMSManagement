using System.Security.Cryptography;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Logging;
using SMSManagement.Modules.Coupon.Domain;
using SMSManagement.Modules.Ingestion.Domain;
using SMSManagement.Modules.Ingestion.Processors;
using SMSManagement.Modules.Ingestion.Sources;
using SMSManagement.Modules.Notifications;
using SMSManagement.Modules.Workflow.Domain;
using SMSManagement.Modules.Workflow.Engine;

namespace SMSManagement.Modules.Ingestion.Services;

/// <summary>
/// The single authoritative entry point for moving a file from "found on disk"
/// to "rows enqueued + file archived". Handles:
///   - content-hash deduplication (IngestionBatches.FileHash UNIQUE constraint)
///   - per-source duplicate policy (Skip / Fail / Reprocess)
///   - force re-ingest override
///   - post-process action (Archive / Delete / Leave)
///   - row-level mapping + validation, with rejected rows separated for review
/// </summary>
public sealed class IngestionPipeline : IIngestionPipeline
{
    private readonly AppDbContext _db;
    private readonly IWorkflowEngine _workflow;
    private readonly IAuditLogger _audit;
    private readonly ILogger<IngestionPipeline> _log;

    private readonly Modules.Core.Notifications.IUserNotifier _notify;
    private readonly IEmailSender _email;

    public IngestionPipeline(
        AppDbContext db,
        IWorkflowEngine workflow,
        IAuditLogger audit,
        ILogger<IngestionPipeline> log,
        Modules.Core.Notifications.IUserNotifier notify,
        IEmailSender email)
    {
        _db = db;
        _workflow = workflow;
        _audit = audit;
        _log = log;
        _notify = notify;
        _email = email;
    }

    public async Task<IngestionOutcome> IngestFileAsync(
        Guid projectId,
        Guid settingsId,
        string filePath,
        bool forceReingest,
        CancellationToken ct = default)
    {
        var settings = await _db.Set<IngestionSourceSettings>().FindAsync([settingsId], ct)
                       ?? throw new InvalidOperationException($"Settings {settingsId} not found");

        var hash = await ComputeFileHashAsync(filePath, ct);

        // 1) Dedup decision
        var prior = await _db.IngestionBatches
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.FileHash == hash, ct);

        if (prior is not null && !forceReingest)
        {
            switch (settings.DuplicatePolicy)
            {
                case DuplicatePolicy.Skip:
                    _log.LogInformation("Duplicate file skipped hash={Hash} priorBatch={BatchId}",
                        hash, prior.Id);
                    await TryMoveAsync(filePath, settings.ArchiveDirectory, ".duplicate", ct);
                    return new IngestionOutcome(prior.Id, IngestionResult.SkippedDuplicate,
                        prior.TotalRows, prior.AcceptedRows, prior.RejectedRows,
                        $"duplicate of batch {prior.Id}");

                case DuplicatePolicy.Fail:
                    await TryMoveAsync(filePath, settings.RejectedDirectory, ".rejected", ct);
                    return new IngestionOutcome(prior.Id, IngestionResult.Rejected,
                        0, 0, 0, "duplicate file rejected by policy");

                case DuplicatePolicy.Reprocess:
                    // Falls through to a fresh batch — note "reprocess" in audit.
                    _log.LogWarning("Reprocessing duplicate file (policy=Reprocess) hash={Hash}", hash);
                    break;
            }
        }

        // 2) Open a fresh batch (the FileHash UNIQUE index requires we suffix re-runs)
        var batch = new IngestionBatch
        {
            ProjectId = projectId,
            SourceType = settings.SourceType,
            SourceRef = Path.GetFileName(filePath),
            FileHash = forceReingest || prior is not null
                ? $"{hash}#{DateTimeOffset.UtcNow.Ticks}"   // forced re-ingest keeps a distinct row
                : hash,
            Status = "Processing"
        };
        _db.IngestionBatches.Add(batch);
        await _db.SaveChangesAsync(ct);

        // 3) Read + map + dispatch. If ProcessRowsAsync throws (bad workflow
        //    JSON, decrypt failure, …) the batch must not be left stuck on
        //    "Processing" — flip it to "Failed", persist, rethrow. Without this
        //    a crashed run orphans a "Processing" row + its unique FileHash,
        //    blocking a clean retry of the same file.
        int accepted, rejected;
        string? rejectionsJson;
        try
        {
            (accepted, rejected, rejectionsJson) =
                await ProcessRowsAsync(projectId, batch.Id, filePath, settingsId,
                    settings.WorkflowName, ct);
        }
        catch (Exception ex)
        {
            batch.Status = "Failed";
            batch.RejectionsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                truncated = false, shown = 0, total = 0, fatal = ex.Message
            });
            await _db.SaveChangesAsync(CancellationToken.None);
            await TryMoveAsync(filePath, settings.RejectedDirectory, ".rejected", CancellationToken.None);
            throw;
        }

        batch.TotalRows = accepted + rejected;
        batch.AcceptedRows = accepted;
        batch.RejectedRows = rejected;
        batch.RejectionsJson = rejectionsJson;
        // Row-level semantics: partial files still complete. Status only flips
        // to "Failed" when NOTHING got through (zero accepted with at least
        // one rejected). Otherwise the batch is "Completed" — operator sees
        // the accepted / rejected split in the UI and clicks through to the
        // rejection sample to fix and re-upload.
        batch.Status = rejected > 0 && accepted == 0 ? "Failed" : "Completed";
        await _db.SaveChangesAsync(ct);

        // 4) Post-process: move/delete/leave
        var result = forceReingest ? IngestionResult.Reprocessed : IngestionResult.Ingested;
        if (batch.Status == "Completed")
            await ApplyPostProcessAsync(filePath, settings, ct);
        else
            await TryMoveAsync(filePath, settings.RejectedDirectory, ".rejected", ct);

        await _audit.WriteAsync(new AuditEntry(
            Guid.Empty,
            "ingestion.batch.complete",
            "IngestionBatch", batch.Id.ToString(),
            string.Empty, string.Empty, string.Empty,
            After: new
            {
                batch.FileHash, batch.TotalRows, batch.AcceptedRows,
                batch.RejectedRows, batch.Status, ForceReingest = forceReingest
            }), ct);

        // Live notify project members. variant=danger when status=Failed
        // (zero rows made it through); warning when some were rejected;
        // success otherwise. The Sources tab Batches list also updates
        // on its own polling timer, but the toast brings the operator
        // attention to it without staring at the tab.
        var variant = batch.Status == "Failed" ? "danger"
                    : batch.RejectedRows > 0 ? "warning"
                    : "success";
        await _notify.ToProjectAsync(projectId, new Modules.Core.Notifications.NotificationPayload(
            Kind: "ingestion.batch.complete",
            Title: $"Batch {batch.Status.ToLowerInvariant()}",
            Body: $"{batch.AcceptedRows} accepted · {batch.RejectedRows} rejected · {batch.TotalRows} total",
            Variant: variant), ct);

        // Fire-and-forget stakeholder email. Tolerant of missing recipients /
        // SMTP misconfig (see SmtpEmailSender). Enqueued via Hangfire so SMTP
        // latency doesn't block the API response, and so the email goes out
        // even if the current request fails after this point.
        try
        {
            BackgroundJob.Enqueue<IIngestionBatchNotifier>(
                n => n.NotifyBatchCompleteAsync(batch.Id, CancellationToken.None));
        }
        catch (Exception ex)
        {
            // Hangfire storage not configured (e.g. test mode) — log + continue.
            _log.LogWarning(ex, "Could not enqueue batch notification for {BatchId}", batch.Id);
        }

        return new IngestionOutcome(batch.Id, result,
            batch.TotalRows, batch.AcceptedRows, batch.RejectedRows, null);
    }

    // ---------------- helpers ----------------

    private async Task<(int accepted, int rejected, string? rejectionsJson)> ProcessRowsAsync(
        Guid projectId, Guid batchId, string filePath, Guid sourceId,
        string? workflowName, CancellationToken ct)
    {
        // Per-source (per-pipeline) config: use this source's own column
        // mappings / validation rules; fall back to the project-shared ones
        // (SourceId == null) when the source has none of its own.
        var allMappings = await _db.ColumnMappings
            .Where(m => m.ProjectId == projectId)
            .ToListAsync(ct);
        var mappings = allMappings.Where(m => m.SourceId == sourceId).ToList();
        if (mappings.Count == 0)
            mappings = allMappings.Where(m => m.SourceId == null).ToList();

        var allRules = await _db.CanonicalFieldRules
            .Where(r => r.ProjectId == projectId)
            .ToListAsync(ct);
        var sourceRules = allRules.Where(r => r.SourceId == sourceId).ToList();
        if (sourceRules.Count == 0)
            sourceRules = allRules.Where(r => r.SourceId == null).ToList();
        var rules = sourceRules.ToDictionary(r => r.CanonicalField, r => r,
            StringComparer.OrdinalIgnoreCase);

        var mapper = new ColumnMapper(mappings, rules);

        // Resolve the workflow this source starts. A source may be bound to a
        // specific workflow by name (a project can run several); otherwise the
        // single active workflow is used. Either way we take the active
        // version, so a newly-published version applies automatically. The
        // JSON is fetched too so we can see whether the workflow issues coupons.
        var defQuery = _db.WorkflowDefinitions
            .Where(d => d.ProjectId == projectId && d.Active);
        if (!string.IsNullOrWhiteSpace(workflowName))
            defQuery = defQuery.Where(d => d.Name == workflowName);

        var def = await defQuery
            .OrderByDescending(d => d.Version)
            .Select(d => new { d.Id, d.DefinitionJson })
            .FirstOrDefaultAsync(ct);

        if (def is null)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(workflowName)
                ? $"Project {projectId} has no active workflow definition."
                : $"Project {projectId} has no active workflow named '{workflowName}'.");

        var couponBatchIds = ExtractCouponBatchIds(def.DefinitionJson);

        // Pick the parser by extension — controller already whitelists .csv/.xlsx.
        IIngestionSource source = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".xlsx" or ".xls" => new ExcelUploadSource(),
            _                 => new CsvUploadSource()
        };
        var context = new IngestionContext(projectId, batchId, filePath,
            new Dictionary<string, string>());

        const int rejectionSampleCap = 50;
        var rejectionSamples = new List<object>(rejectionSampleCap);
        // Valid rows are buffered so the coupon sufficiency check can run
        // BEFORE any workflow instance is started — a shortage must block the
        // whole run, not leave it half-sent.
        var validRows = new List<IReadOnlyDictionary<string, string>>();
        var rejected = 0; var rowIndex = 0;

        await foreach (var raw in source.ReadAsync(context, ct))
        {
            rowIndex++;
            var mapped = mapper.Map(raw);
            if (!mapped.IsValid)
            {
                rejected++;
                _log.LogWarning("Row rejected batch={BatchId} rowIndex={RowIndex} errors={Errors}",
                    batchId, rowIndex, string.Join(',', mapped.Errors));
                if (rejectionSamples.Count < rejectionSampleCap)
                    rejectionSamples.Add(new
                    {
                        rowIndex,
                        errors = mapped.Errors,
                        // Plain-language reasons for non-technical operators.
                        reasons = mapped.Errors
                            .Select(RejectionHumanizer.Describe)
                            .Distinct()
                            .ToArray()
                    });
                continue;
            }
            validRows.Add(mapped.Row);
        }

        // Coupon sufficiency pre-check. Throws (failing the whole batch) when a
        // coupon batch can't cover every recipient — nothing is sent.
        if (couponBatchIds.Count > 0 && validRows.Count > 0)
            await EnsureCouponSufficiencyAsync(projectId, couponBatchIds, validRows.Count, ct);

        foreach (var row in validRows)
            await _workflow.StartAsync(def.Id, row, batchId, ct);
        var accepted = validRows.Count;

        var rejectionsJson = rejected == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(new
            {
                truncated = rejected > rejectionSampleCap,
                shown = rejectionSamples.Count,
                total = rejected,
                items = rejectionSamples
            });

        return (accepted, rejected, rejectionsJson);
    }

    private static readonly System.Text.Json.JsonSerializerOptions SpecJsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>Coupon batch ids drawn from any <c>issue_coupon</c> step of the
    /// workflow spec. Empty when the workflow issues no coupons.</summary>
    private static List<Guid> ExtractCouponBatchIds(string definitionJson)
    {
        try
        {
            var spec = System.Text.Json.JsonSerializer
                .Deserialize<WorkflowSpec>(definitionJson, SpecJsonOpts);
            if (spec?.Steps is null) return new();
            return spec.Steps.Values
                .Where(s => string.Equals(s.Type, "issue_coupon", StringComparison.OrdinalIgnoreCase)
                            && s.CouponBatchId is not null)
                .Select(s => s.CouponBatchId!.Value)
                .Distinct()
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>
    /// Blocks the run when a coupon batch has fewer Available coupons than the
    /// run has recipients (one coupon per recipient per issue_coupon step).
    /// Sends an email alert and throws so the whole batch fails — unless the
    /// project has turned the check off.
    /// </summary>
    private async Task EnsureCouponSufficiencyAsync(
        Guid projectId, List<Guid> couponBatchIds, int recipientCount, CancellationToken ct)
    {
        var project = await _db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Name, p.NotificationEmails, p.CouponCheckEnabled, p.EmailAlertsEnabled })
            .FirstOrDefaultAsync(ct);
        if (project is null || !project.CouponCheckEnabled) return;   // check disabled — allow

        var shortfalls = new List<string>();
        foreach (var bid in couponBatchIds)
        {
            var available = await _db.Coupons
                .CountAsync(c => c.BatchId == bid && c.Status == CouponStatus.Available, ct);
            if (available < recipientCount)
            {
                var name = await _db.CouponBatches
                    .Where(b => b.Id == bid).Select(b => b.Name)
                    .FirstOrDefaultAsync(ct) ?? bid.ToString();
                shortfalls.Add($"ชุดคูปอง '{name}' มีคงเหลือ {available} ใบ "
                             + $"แต่ต้องการ {recipientCount} ใบ (ขาด {recipientCount - available})");
            }
        }
        if (shortfalls.Count == 0) return;

        var detail = string.Join("; ", shortfalls);
        _log.LogError(
            "Coupon shortage blocked an ingestion run for project {ProjectId}: {Detail}",
            projectId, detail);

        var recipients = IngestionBatchNotifier.ParseRecipients(project.NotificationEmails);
        if (project.EmailAlertsEnabled && recipients.Length > 0)
        {
            string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
            var subject = $"[{project.Name}] คูปองไม่พอ — แคมเปญถูกระงับ";
            var html =
                "<h3>แคมเปญถูกระงับ: คูปองไม่เพียงพอ</h3>"
                + $"<p>โปรเจ็ค <strong>{Enc(project.Name)}</strong> มีผู้รับ "
                + $"<strong>{recipientCount}</strong> ราย แต่คูปองคงเหลือไม่พอ — "
                + "ระบบ<strong>ไม่ส่งข้อความทั้งรอบ</strong> เพื่อไม่ให้บางคนไม่ได้คูปอง</p>"
                + "<ul>" + string.Join("", shortfalls.Select(s => $"<li>{Enc(s)}</li>")) + "</ul>"
                + "<p>โปรดเติมคูปองในชุดที่เกี่ยวข้อง แล้วนำเข้าไฟล์อีกครั้ง</p>";
            var text = $"แคมเปญถูกระงับ: คูปองไม่พอ\nผู้รับ {recipientCount} ราย\n{detail}";
            try { await _email.SendAsync(new EmailMessage(recipients, subject, html, text), ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Coupon-shortage alert email failed."); }
        }

        throw new InvalidOperationException(
            $"คูปองไม่พอกับจำนวนผู้รับ ({recipientCount} ราย) — ระงับการส่งทั้งรอบ. {detail}");
    }

    private async Task ApplyPostProcessAsync(
        string filePath, IngestionSourceSettings settings, CancellationToken ct)
    {
        // SFTP files are a throwaway local temp copy. The poller archives the
        // *remote* file on the SFTP server (RemoteDirectory/archive) and
        // deletes the temp itself — so the local Archive/Delete action here is
        // meaningless. Worse, an operator who types an SFTP-style path into
        // ArchiveDirectory (e.g. "C:\sftp\archive") hits a denied local path,
        // which would wrongly fail an otherwise-successful ingestion and send
        // the remote file to /error instead of /archive.
        if (string.Equals(settings.SourceType, "SFTP", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogDebug(
                "SFTP source {SourceId}: skipping local post-process — the poller "
                + "archives the remote file on the SFTP server.", settings.Id);
            return;
        }

        switch (settings.Action)
        {
            case PostProcessAction.Archive:
                // A missing archive directory must not abort the run — the rows
                // were already ingested. Log and leave the file; failing here
                // would also block the SFTP poller from archiving the remote
                // copy, making it re-poll the same file forever.
                if (string.IsNullOrEmpty(settings.ArchiveDirectory))
                {
                    _log.LogWarning(
                        "PostProcess=Archive but ArchiveDirectory not configured for "
                        + "source {SourceId} — file left in place.", settings.Id);
                    break;
                }
                await TryMoveAsync(filePath, settings.ArchiveDirectory, string.Empty, ct);
                break;

            case PostProcessAction.Delete:
                File.Delete(filePath);
                break;

            case PostProcessAction.Leave:
                // Intentional no-op; dedup table prevents re-pickup of the same content hash.
                break;
        }
    }

    private Task TryMoveAsync(string filePath, string? destDir, string suffix, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(destDir) || !File.Exists(filePath))
            return Task.CompletedTask;

        try
        {
            Directory.CreateDirectory(destDir);
            var name = Path.GetFileName(filePath);
            // Suffix + timestamp guarantees no clobber if the same filename arrives twice.
            var ts = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            var dest = Path.Combine(destDir, $"{Path.GetFileNameWithoutExtension(name)}.{ts}{suffix}{Path.GetExtension(name)}");
            File.Move(filePath, dest, overwrite: false);
        }
        catch (Exception ex)
        {
            // Relocating the local file is best-effort — the rows are already
            // ingested. A denied/missing path must never fail the run.
            _log.LogWarning(ex,
                "Could not move processed file to '{Dest}' — leaving it in place.", destDir);
        }
        return Task.CompletedTask;
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }
}

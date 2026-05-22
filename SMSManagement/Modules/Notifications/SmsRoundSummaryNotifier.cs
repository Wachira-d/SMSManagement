using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Ingestion.Domain;
using SMSManagement.Modules.Shortlink.Services;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Workflow.Domain;

namespace SMSManagement.Modules.Notifications;

public interface ISmsRoundSummaryNotifier
{
    /// <summary>Recurring sweep: for every ingestion batch whose SMS have all
    /// finished sending, emails the project a per-round summary with a CSV log
    /// attached, then stamps the batch so it is summarised exactly once.</summary>
    Task SweepAsync(CancellationToken ct = default);
}

/// <summary>
/// Sends the per-round SMS summary. A "round" is one ingestion batch. The
/// round is summarised once every workflow it started is terminal and every
/// SMS has reached a final delivery state (Delivered/Failed/Rejected/Expired)
/// — "Sent" is NOT final, since a delivery notification later flips it to
/// Delivered/Failed, so the summary waits for the DLR. As a backstop (no DLR
/// configured, or a workflow stuck awaiting action), a round is summarised
/// anyway once it is older than <see cref="MaxWait"/>. Tolerant of misconfig —
/// a summarised round is stamped so it is never re-scanned.
/// </summary>
public sealed class SmsRoundSummaryNotifier : ISmsRoundSummaryNotifier
{
    // Grace period after ingestion before a batch is considered — gives the
    // workflow engine time to tick and enqueue the round's SMS.
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);
    // Backstop: summarise a round even if SMS never reach a final delivery
    // state (no DLR wired) or a workflow is stuck non-terminal.
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(24);
    private const int BatchScanCap = 25;

    private readonly AppDbContext _db;
    private readonly IEmailSender _email;
    private readonly FieldEncryptor _crypto;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<ShortlinkOptions> _slOpts;
    private readonly ILogger<SmsRoundSummaryNotifier> _log;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public SmsRoundSummaryNotifier(
        AppDbContext db, IEmailSender email, FieldEncryptor crypto, TimeProvider clock,
        IOptionsMonitor<ShortlinkOptions> slOpts,
        ILogger<SmsRoundSummaryNotifier> log)
    {
        _db = db;
        _email = email;
        _crypto = crypto;
        _clock = clock;
        _slOpts = slOpts;
        _log = log;
    }

    public async Task SweepAsync(CancellationToken ct = default)
    {
        var cutoff = _clock.GetUtcNow() - Grace;
        var batches = await _db.IngestionBatches
            .IgnoreQueryFilters()
            .Where(b => b.SmsSummarySentAt == null && b.IngestedAt < cutoff)
            .OrderBy(b => b.IngestedAt)
            .Take(BatchScanCap)
            .ToListAsync(ct);

        foreach (var batch in batches)
        {
            try { await TryNotifyAsync(batch, ct); }
            catch (Exception ex)
            {
                _log.LogError(ex, "SMS round summary failed for batch {BatchId}", batch.Id);
            }
        }
    }

    private sealed record RoundSms(
        Guid InstanceId, string Provider, string? SenderId, SmsStatus Status,
        short Attempts, DateTimeOffset CreatedAt, DateTimeOffset? SentAt,
        DateTimeOffset? DeliveredAt, string? ErrorCode, string? ProviderMessageId,
        string? RawProviderResponse, byte[] EncryptedBody);

    private sealed record ClickRow(Guid ShortlinkId, DateTimeOffset ClickedAt);

    /// <summary>One report row = one recipient (workflow instance): the mapped
    /// source columns joined with the SMS outcome and shortlink click activity.</summary>
    private sealed record RoundRow(
        string MaskedPhone, WorkflowState State,
        IReadOnlyDictionary<string, string> Source,
        int SmsCount, string? Provider, string? SenderId, SmsStatus? SmsStatus,
        short Attempts, DateTimeOffset? SentAt, DateTimeOffset? DeliveredAt,
        string? ErrorCode, string? ProviderMessageId, string? ProviderResponse,
        string Body, string? ShortlinkUrl, string? ShortlinkTarget,
        int ClickCount, DateTimeOffset? FirstClickedAt);

    private async Task TryNotifyAsync(IngestionBatch batch, CancellationToken ct)
    {
        // Past MaxWait we summarise whatever state the round is in — covers a
        // workflow stuck non-terminal or SMS that never get a delivery report.
        var expired = _clock.GetUtcNow() - batch.IngestedAt > MaxWait;

        // 1. Every workflow instance the batch started — also carries the
        //    mapped source row (EncryptedPayload) used in the report.
        var instances = await _db.WorkflowInstances
            .IgnoreQueryFilters()
            .Where(w => w.IngestionBatchId == batch.Id)
            .Select(w => new { w.Id, w.State, w.MaskedPhone, w.EncryptedPayload })
            .ToListAsync(ct);

        if (!expired && instances.Any(w => w.State is not (
                WorkflowState.Completed or WorkflowState.Failed or WorkflowState.Expired)))
            return;

        // 2. Collect the SMS produced by the round.
        var ids = instances.Select(w => w.Id).ToHashSet();
        var sms = await _db.SmsMessages
            .IgnoreQueryFilters()
            .Where(m => m.WorkflowInstanceId != null && ids.Contains(m.WorkflowInstanceId.Value))
            .Select(m => new RoundSms(
                m.WorkflowInstanceId!.Value, m.Provider, m.SenderId, m.Status, m.Attempts,
                m.CreatedAt, m.SentAt, m.DeliveredAt, m.ErrorCode, m.ProviderMessageId,
                m.RawProviderResponse, m.EncryptedBody))
            .ToListAsync(ct);

        // 3. Wait until every SMS has reached a final delivery state. "Sent"
        //    (gateway-accepted) is NOT final — a DLR later flips it to
        //    Delivered/Failed, so the summary would otherwise report stale
        //    counts. Past MaxWait we stop waiting.
        if (!expired && sms.Any(m => m.Status is
                SmsStatus.Queued or SmsStatus.Sending or SmsStatus.Sent))
            return;

        // The round is settled — stamp now so it is summarised exactly once,
        // regardless of whether an email actually goes out below.
        batch.SmsSummarySentAt = _clock.GetUtcNow();

        if (instances.Count == 0)
        {
            await _db.SaveChangesAsync(ct);   // nothing to report
            return;
        }

        // 4. Shortlinks + click activity for the round's instances.
        var shortlinks = await _db.Shortlinks
            .IgnoreQueryFilters()
            .Where(s => s.WorkflowInstanceId != null && ids.Contains(s.WorkflowInstanceId.Value))
            .Select(s => new { s.Id, s.WorkflowInstanceId, s.Slug, s.EncryptedTargetUrl, s.ClickCount })
            .ToListAsync(ct);
        var slIds = shortlinks.Select(s => s.Id).ToList();
        var clicks = slIds.Count == 0
            ? new List<ClickRow>()
            : await _db.ShortlinkClicks
                .Where(c => slIds.Contains(c.ShortlinkId))
                .Select(c => new ClickRow(c.ShortlinkId, c.ClickedAt))
                .ToListAsync(ct);

        var project = await _db.Projects
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == batch.ProjectId, ct);

        // Assemble one report row per recipient.
        var baseUrl = (project?.ShortlinkBaseUrl is { Length: > 0 } pb
                ? pb : _slOpts.CurrentValue.PublicBaseUrl ?? string.Empty)
            .TrimEnd('/');
        var smsByInstance = sms.GroupBy(m => m.InstanceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).ToList());
        var slByInstance = shortlinks
            .Where(s => s.WorkflowInstanceId != null)
            .GroupBy(s => s.WorkflowInstanceId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var firstClickBySl = clicks.GroupBy(c => c.ShortlinkId)
            .ToDictionary(g => g.Key, g => g.Min(c => c.ClickedAt));

        var rows = new List<RoundRow>(instances.Count);
        foreach (var inst in instances)
        {
            Dictionary<string, string> src;
            try
            {
                src = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    _crypto.Decrypt(inst.EncryptedPayload), JsonOpts) ?? new();
            }
            catch { src = new(); }

            var msgs = smsByInstance.GetValueOrDefault(inst.Id);
            var latest = msgs?.FirstOrDefault();
            var sl = slByInstance.GetValueOrDefault(inst.Id);
            var slUrl = sl is null ? null
                : string.IsNullOrEmpty(baseUrl) ? sl.Slug : $"{baseUrl}/{sl.Slug}";
            DateTimeOffset? firstClick =
                sl is not null && firstClickBySl.TryGetValue(sl.Id, out var fc) ? fc : null;

            rows.Add(new RoundRow(
                inst.MaskedPhone, inst.State, src,
                msgs?.Count ?? 0,
                latest?.Provider, latest?.SenderId, latest?.Status,
                latest?.Attempts ?? 0, latest?.SentAt, latest?.DeliveredAt,
                latest?.ErrorCode, latest?.ProviderMessageId, latest?.RawProviderResponse,
                latest is null ? string.Empty : Decrypt(latest.EncryptedBody),
                slUrl, sl is null ? null : Decrypt(sl.EncryptedTargetUrl),
                sl?.ClickCount ?? 0, firstClick));
        }

        // Log the outcome explicitly so an operator can see WHY a round did or
        // did not produce a summary email (the single most common support
        // question). Warning-level skips surface in /Admin/ErrorLogs.
        if (project is null)
        {
            _log.LogWarning(
                "SMS round summary: batch {BatchId} — project {ProjectId} not found.",
                batch.Id, batch.ProjectId);
        }
        else if (!project.EmailAlertsEnabled || !project.NotifyOnSmsRoundComplete)
        {
            _log.LogWarning(
                "SMS round summary skipped for batch {BatchId}: project '{Project}' has "
                + "EmailAlertsEnabled={Alerts}, NotifyOnSmsRoundComplete={Notify}.",
                batch.Id, project.Name, project.EmailAlertsEnabled,
                project.NotifyOnSmsRoundComplete);
        }
        else
        {
            var recipients = IngestionBatchNotifier.ParseRecipients(project.NotificationEmails);
            if (recipients.Length == 0)
            {
                _log.LogWarning(
                    "SMS round summary skipped for batch {BatchId}: project '{Project}' has "
                    + "no notification email addresses configured.", batch.Id, project.Name);
            }
            else
            {
                await _email.SendAsync(BuildEmail(project, batch, sms, rows, recipients), ct);
                _log.LogInformation(
                    "SMS round summary handed to the email sender for batch {BatchId} — "
                    + "{Count} recipient(s): {Recipients}.",
                    batch.Id, recipients.Length, string.Join(", ", recipients));
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private EmailMessage BuildEmail(
        Project project, IngestionBatch batch, IReadOnlyList<RoundSms> sms,
        IReadOnlyList<RoundRow> rows, string[] recipients)
    {
        int delivered = sms.Count(m => m.Status == SmsStatus.Delivered);
        int sent      = sms.Count(m => m.Status == SmsStatus.Sent);
        int failed    = sms.Count(m => m.Status is SmsStatus.Failed
                                              or SmsStatus.Rejected or SmsStatus.Expired);
        int success   = delivered + sent;
        int clicked   = rows.Count(r => r.ClickCount > 0);

        var prefix = string.IsNullOrWhiteSpace(project.NotificationSubjectPrefix)
            ? $"[{project.Name}]"
            : project.NotificationSubjectPrefix.Trim();
        var subject = $"{prefix} SMS round summary: {success} sent / {failed} failed "
                    + $"({rows.Count} recipients)";

        var enc = (string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        var html = $"""
            <h2>Campaign Platform — SMS Round Summary</h2>
            <p>Project: <strong>{enc(project.Name)}</strong></p>
            <table border="1" cellpadding="6" cellspacing="0">
              <tr><td>Round (batch)</td><td>{batch.Id}</td></tr>
              <tr><td>Source</td><td>{enc(batch.SourceType)} — {enc(batch.SourceRef)}</td></tr>
              <tr><td>Recipients</td><td>{rows.Count}</td></tr>
              <tr style="color:green"><td>Delivered (confirmed by DLR)</td><td>{delivered}</td></tr>
              <tr><td>Sent (accepted by gateway)</td><td>{sent}</td></tr>
              <tr style="color:#c00"><td>Failed / Rejected / Expired</td><td>{failed}</td></tr>
              <tr style="color:#0a6"><td>Clicked the link</td><td>{clicked}</td></tr>
            </table>
            <p>The attached CSV is a per-recipient report: the original mapped
               source columns plus shortlink URL, click activity, send status
               and delivery status for every recipient.</p>
            <p style="font-size:smaller;color:#666">Generated automatically. Do not reply.</p>
            """;
        var text = $"""
            Campaign Platform — SMS Round Summary
            Project: {project.Name}
            Round:   {batch.Id}
            Source:  {batch.SourceType} — {batch.SourceRef}
            Recipients: {rows.Count}
            Delivered:  {delivered}
            Sent:       {sent}
            Failed:     {failed}
            Clicked:    {clicked}
            See the attached CSV for the full per-recipient report.
            """;

        var csv = new EmailAttachment(
            $"sms-round-{batch.Id:N}.csv", "text/csv", BuildCsv(rows));

        return new EmailMessage(recipients, subject, html, text, new[] { csv });
    }

    /// <summary>Per-recipient CSV: the dynamic mapped source columns followed
    /// by the campaign outcome — shortlink, clicks, send + delivery status.</summary>
    private byte[] BuildCsv(IReadOnlyList<RoundRow> rows)
    {
        var sourceCols = rows
            .SelectMany(r => r.Source.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.Append('﻿');   // UTF-8 BOM — Excel opens it cleanly
        var header = sourceCols.Select(c => "src_" + c)
            .Concat(new[]
            {
                "Recipient", "WorkflowState", "SmsCount", "SmsStatus",
                "SentAt", "DeliveredAt", "Attempts", "Provider", "Sender",
                "ErrorCode", "ProviderMessageId", "ProviderResponse", "MessageBody",
                "ShortlinkUrl", "ShortlinkTarget", "Clicked", "ClickCount", "FirstClickedAt"
            });
        sb.AppendLine(string.Join(',', header.Select(Csv)));

        foreach (var r in rows)
        {
            var cells = new List<string>(sourceCols.Count + 18);
            foreach (var c in sourceCols)
                cells.Add(Csv(r.Source.TryGetValue(c, out var v) ? v : string.Empty));
            cells.Add(Csv(r.MaskedPhone));
            cells.Add(Csv(r.State.ToString()));
            cells.Add(Csv(r.SmsCount.ToString()));
            cells.Add(Csv(r.SmsStatus?.ToString()));
            cells.Add(Csv(r.SentAt?.ToString("u")));
            cells.Add(Csv(r.DeliveredAt?.ToString("u")));
            cells.Add(Csv(r.Attempts.ToString()));
            cells.Add(Csv(r.Provider));
            cells.Add(Csv(r.SenderId));
            cells.Add(Csv(r.ErrorCode));
            cells.Add(Csv(r.ProviderMessageId));
            cells.Add(Csv(r.ProviderResponse));
            cells.Add(Csv(r.Body));
            cells.Add(Csv(r.ShortlinkUrl));
            cells.Add(Csv(r.ShortlinkTarget));
            cells.Add(Csv(r.ClickCount > 0 ? "yes" : "no"));
            cells.Add(Csv(r.ClickCount.ToString()));
            cells.Add(Csv(r.FirstClickedAt?.ToString("u")));
            sb.AppendLine(string.Join(',', cells));
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private string Decrypt(byte[] cipher)
    {
        try { return _crypto.Decrypt(cipher); }
        catch { return "(decrypt failed)"; }
    }

    private static string Csv(string? v)
    {
        v ??= string.Empty;
        return v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }
}

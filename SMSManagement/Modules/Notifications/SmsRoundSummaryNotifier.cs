using System.Text;
using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Ingestion.Domain;
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
    private readonly ILogger<SmsRoundSummaryNotifier> _log;

    public SmsRoundSummaryNotifier(
        AppDbContext db, IEmailSender email, FieldEncryptor crypto, TimeProvider clock,
        ILogger<SmsRoundSummaryNotifier> log)
    {
        _db = db;
        _email = email;
        _crypto = crypto;
        _clock = clock;
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
        byte[] EncryptedTo, byte[] EncryptedBody, string Provider, string? SenderId,
        SmsStatus Status, string? ProviderMessageId, string? ErrorCode,
        string? RawProviderResponse, DateTimeOffset? SentAt, DateTimeOffset? DeliveredAt);

    private async Task TryNotifyAsync(IngestionBatch batch, CancellationToken ct)
    {
        // Past MaxWait we summarise whatever state the round is in — covers a
        // workflow stuck non-terminal or SMS that never get a delivery report.
        var expired = _clock.GetUtcNow() - batch.IngestedAt > MaxWait;

        // 1. Every workflow the batch started must be terminal — otherwise more
        //    SMS may still be enqueued and the round is not over.
        var workflows = await _db.WorkflowInstances
            .IgnoreQueryFilters()
            .Where(w => w.IngestionBatchId == batch.Id)
            .Select(w => new { w.Id, w.State })
            .ToListAsync(ct);

        if (!expired && workflows.Any(w => w.State is not (
                WorkflowState.Completed or WorkflowState.Failed or WorkflowState.Expired)))
            return;

        // 2. Collect the SMS produced by the round.
        var ids = workflows.Select(w => w.Id).ToHashSet();
        var sms = await _db.SmsMessages
            .IgnoreQueryFilters()
            .Where(m => m.WorkflowInstanceId != null && ids.Contains(m.WorkflowInstanceId.Value))
            .Select(m => new RoundSms(
                m.EncryptedTo, m.EncryptedBody, m.Provider, m.SenderId, m.Status,
                m.ProviderMessageId, m.ErrorCode, m.RawProviderResponse,
                m.SentAt, m.DeliveredAt))
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

        if (sms.Count == 0)
        {
            await _db.SaveChangesAsync(ct);   // 0-SMS round (e.g. all rows rejected)
            return;
        }

        var project = await _db.Projects
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == batch.ProjectId, ct);

        if (project is { EmailAlertsEnabled: true, NotifyOnSmsRoundComplete: true })
        {
            var recipients = IngestionBatchNotifier.ParseRecipients(project.NotificationEmails);
            if (recipients.Length > 0)
                await _email.SendAsync(BuildEmail(project, batch, sms, recipients), ct);
            else
                _log.LogDebug("Batch {BatchId}: no SMS-summary recipients configured.", batch.Id);
        }

        await _db.SaveChangesAsync(ct);
    }

    private EmailMessage BuildEmail(
        Project project, IngestionBatch batch, IReadOnlyList<RoundSms> sms, string[] recipients)
    {
        int delivered = sms.Count(m => m.Status == SmsStatus.Delivered);
        int sent      = sms.Count(m => m.Status == SmsStatus.Sent);
        int failed    = sms.Count(m => m.Status is SmsStatus.Failed
                                              or SmsStatus.Rejected or SmsStatus.Expired);
        int success   = delivered + sent;

        var prefix = string.IsNullOrWhiteSpace(project.NotificationSubjectPrefix)
            ? $"[{project.Name}]"
            : project.NotificationSubjectPrefix.Trim();
        var subject = $"{prefix} SMS round summary: {success} sent / {failed} failed "
                    + $"({sms.Count} total)";

        var enc = (string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        var html = $"""
            <h2>Campaign Platform — SMS Round Summary</h2>
            <p>Project: <strong>{enc(project.Name)}</strong></p>
            <table border="1" cellpadding="6" cellspacing="0">
              <tr><td>Round (batch)</td><td>{batch.Id}</td></tr>
              <tr><td>Source</td><td>{enc(batch.SourceType)} — {enc(batch.SourceRef)}</td></tr>
              <tr><td>Total SMS</td><td>{sms.Count}</td></tr>
              <tr style="color:green"><td>Delivered (confirmed)</td><td>{delivered}</td></tr>
              <tr><td>Sent (accepted by gateway)</td><td>{sent}</td></tr>
              <tr style="color:#c00"><td>Failed / Rejected / Expired</td><td>{failed}</td></tr>
            </table>
            <p>The attached CSV lists every message in this round.</p>
            <p style="font-size:smaller;color:#666">Generated automatically. Do not reply.</p>
            """;
        var text = $"""
            Campaign Platform — SMS Round Summary
            Project: {project.Name}
            Round:   {batch.Id}
            Source:  {batch.SourceType} — {batch.SourceRef}
            Total:   {sms.Count}
            Delivered: {delivered}
            Sent:      {sent}
            Failed:    {failed}
            See the attached CSV for the full per-message log.
            """;

        var csv = new EmailAttachment(
            $"sms-round-{batch.Id:N}.csv", "text/csv", BuildCsv(sms));

        return new EmailMessage(recipients, subject, html, text, new[] { csv });
    }

    private byte[] BuildCsv(IReadOnlyList<RoundSms> sms)
    {
        var sb = new StringBuilder();
        sb.Append('﻿');   // UTF-8 BOM — Excel opens it cleanly
        sb.AppendLine("Recipient,Message,Status,Provider,Sender,"
                    + "ProviderMessageId,ErrorCode,ProviderResponse,SentAt,DeliveredAt");
        foreach (var m in sms)
        {
            sb.AppendLine(string.Join(',', new[]
            {
                Csv(Decrypt(m.EncryptedTo)),
                Csv(Decrypt(m.EncryptedBody)),
                Csv(m.Status.ToString()),
                Csv(m.Provider),
                Csv(m.SenderId),
                Csv(m.ProviderMessageId),
                Csv(m.ErrorCode),
                Csv(m.RawProviderResponse),
                Csv(m.SentAt?.ToString("u")),
                Csv(m.DeliveredAt?.ToString("u"))
            }));
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

using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Ingestion.Domain;

namespace SMSManagement.Modules.Notifications;

public interface ISmsRoundSummaryNotifier
{
    /// <summary>Recurring sweep: for every ingestion batch whose SMS have all
    /// finished sending, emails the project a per-round summary with a CSV
    /// report attached, then stamps the batch so it is summarised once.</summary>
    Task SweepAsync(CancellationToken ct = default);
}

/// <summary>
/// Sends the per-round SMS summary email. A "round" is one ingestion batch.
/// The round is summarised once every workflow it started is terminal and
/// every SMS has reached a final delivery state — "Sent" is NOT final, since a
/// delivery notification later flips it to Delivered/Failed. As a backstop a
/// round is summarised anyway once older than <see cref="MaxWait"/>. The
/// attached CSV is built by <see cref="IRoundReportService"/> (shared with the
/// on-demand download). A summarised round is stamped so it is never re-sent.
/// </summary>
public sealed class SmsRoundSummaryNotifier : ISmsRoundSummaryNotifier
{
    // Grace period after ingestion before a batch is considered.
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);
    // Backstop: summarise even if SMS never reach a final state / a workflow
    // is stuck non-terminal.
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(24);
    private const int BatchScanCap = 25;

    private readonly AppDbContext _db;
    private readonly IEmailSender _email;
    private readonly IRoundReportService _report;
    private readonly TimeProvider _clock;
    private readonly ILogger<SmsRoundSummaryNotifier> _log;

    public SmsRoundSummaryNotifier(
        AppDbContext db, IEmailSender email, IRoundReportService report,
        TimeProvider clock, ILogger<SmsRoundSummaryNotifier> log)
    {
        _db = db;
        _email = email;
        _report = report;
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

    private async Task TryNotifyAsync(IngestionBatch batch, CancellationToken ct)
    {
        // Past MaxWait we summarise whatever state the round is in.
        var expired = _clock.GetUtcNow() - batch.IngestedAt > MaxWait;

        var report = await _report.BuildAsync(batch.Id, ct);
        if (report is null)
        {
            // The round produced no recipients (e.g. all rows rejected, or a
            // coupon-shortage block). Nothing to summarise — stamp and move on.
            batch.SmsSummarySentAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return;
        }

        // The round isn't over until every workflow is terminal and every SMS
        // has settled (a DLR may still flip a "Sent" to Delivered/Failed).
        if (!expired && (report.AnyNonTerminalWorkflow || report.AnyNonFinalSms))
            return;

        // Settled — stamp now so it is summarised exactly once, whatever
        // happens with the email below.
        batch.SmsSummarySentAt = _clock.GetUtcNow();

        var project = await _db.Projects
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == batch.ProjectId, ct);

        // Log the outcome explicitly so an operator can see WHY a round did or
        // did not produce a summary email. Warning-level skips show in
        // /Admin/ErrorLogs.
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
                await _email.SendAsync(BuildEmail(project, batch, report, recipients), ct);
                _log.LogInformation(
                    "SMS round summary handed to the email sender for batch {BatchId} — "
                    + "{Count} recipient(s): {Recipients}.",
                    batch.Id, recipients.Length, string.Join(", ", recipients));
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private static EmailMessage BuildEmail(
        Project project, IngestionBatch batch, RoundReport report, string[] recipients)
    {
        var success = report.Delivered + report.Sent;

        var prefix = string.IsNullOrWhiteSpace(project.NotificationSubjectPrefix)
            ? $"[{project.Name}]"
            : project.NotificationSubjectPrefix.Trim();
        var subject = $"{prefix} SMS round summary: {success} sent / {report.Failed} failed "
                    + $"({report.Recipients} recipients)";

        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        var html = $"""
            <h2>Campaign Platform — SMS Round Summary</h2>
            <p>Project: <strong>{Enc(project.Name)}</strong></p>
            <table border="1" cellpadding="6" cellspacing="0">
              <tr><td>Round (batch)</td><td>{batch.Id}</td></tr>
              <tr><td>Source</td><td>{Enc(batch.SourceType)} — {Enc(batch.SourceRef)}</td></tr>
              <tr><td>Recipients</td><td>{report.Recipients}</td></tr>
              <tr style="color:green"><td>Delivered (confirmed by DLR)</td><td>{report.Delivered}</td></tr>
              <tr><td>Sent (accepted by gateway)</td><td>{report.Sent}</td></tr>
              <tr style="color:#c00"><td>Failed / Rejected / Expired</td><td>{report.Failed}</td></tr>
              <tr style="color:#0a6"><td>Clicked the link</td><td>{report.Clicked}</td></tr>
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
            Recipients: {report.Recipients}
            Delivered:  {report.Delivered}
            Sent:       {report.Sent}
            Failed:     {report.Failed}
            Clicked:    {report.Clicked}
            See the attached CSV for the full per-recipient report.
            """;

        var csv = new EmailAttachment(
            $"sms-round-{batch.Id:N}.csv", "text/csv", report.Csv);

        return new EmailMessage(recipients, subject, html, text, new[] { csv });
    }
}

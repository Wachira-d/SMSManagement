using Microsoft.EntityFrameworkCore;
using SMSManagement.Infrastructure.Persistence;

namespace SMSManagement.Modules.Notifications;

public interface IIngestionBatchNotifier
{
    /// <summary>Enqueued as a Hangfire fire-and-forget job from the
    /// ingestion pipeline once a batch reaches a terminal state.</summary>
    Task NotifyBatchCompleteAsync(Guid batchId, CancellationToken ct = default);
}

/// <summary>
/// Builds the per-batch summary email and ships it to the project's
/// NotificationEmails distribution list. Fully tolerant of misconfig:
///   - email feature disabled on the project => skip
///   - per-event toggle off (e.g. NotifyOnIngestSuccess=false) => skip
///   - no recipients => skip silently
///   - SMTP not wired => sender logs a warning and returns
/// </summary>
public sealed class IngestionBatchNotifier : IIngestionBatchNotifier
{
    private readonly AppDbContext _db;
    private readonly IEmailSender _email;
    private readonly ILogger<IngestionBatchNotifier> _log;

    public IngestionBatchNotifier(AppDbContext db, IEmailSender email,
        ILogger<IngestionBatchNotifier> log)
    {
        _db = db;
        _email = email;
        _log = log;
    }

    public async Task NotifyBatchCompleteAsync(Guid batchId, CancellationToken ct = default)
    {
        // IgnoreQueryFilters so background jobs always see the data —
        // user-scope filter would hide everything from a SystemUserContext run.
        var batch = await _db.IngestionBatches
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null) { _log.LogWarning("Notify: batch {Id} not found.", batchId); return; }

        var project = await _db.Projects
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == batch.ProjectId, ct);
        if (project is null || !project.EmailAlertsEnabled
            || string.IsNullOrWhiteSpace(project.NotificationEmails))
            return;

        // Per-event filter. The notifier deliberately does the filtering here
        // rather than at enqueue time — operators can flip a toggle and have
        // it take effect for already-in-flight batches without redeploying.
        var category = ClassifyBatch(batch);
        var wantsThisEvent = category switch
        {
            EventCategory.Success => project.NotifyOnIngestSuccess,
            EventCategory.Partial => project.NotifyOnIngestPartial,
            EventCategory.Failure => project.NotifyOnIngestFailure,
            _                     => false
        };
        if (!wantsThisEvent)
        {
            _log.LogDebug("Skipping notify: project {Id} has NotifyOn{Category}=false.",
                project.Id, category);
            return;
        }

        var recipients = ParseRecipients(project.NotificationEmails);
        if (recipients.Length == 0) return;

        var prefix = string.IsNullOrWhiteSpace(project.NotificationSubjectPrefix)
            ? $"[{project.Name}]"
            : project.NotificationSubjectPrefix.Trim();

        var (subject, html, text) = Render(prefix, project.Name, batch, category);

        await _email.SendAsync(new EmailMessage(recipients, subject, html, text), ct);
    }

    public static string[] ParseRecipients(string? csv) =>
        csv?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

    private enum EventCategory { Success, Partial, Failure }

    private static EventCategory ClassifyBatch(Modules.Ingestion.Domain.IngestionBatch b)
    {
        if (b.Status == "Failed" || b.AcceptedRows == 0) return EventCategory.Failure;
        if (b.RejectedRows > 0) return EventCategory.Partial;
        return EventCategory.Success;
    }

    private static (string Subject, string Html, string Text) Render(
        string subjectPrefix,
        string projectName,
        Modules.Ingestion.Domain.IngestionBatch b,
        EventCategory category)
    {
        var label = category switch
        {
            EventCategory.Success => "Completed",
            EventCategory.Partial => "Partial",
            EventCategory.Failure => "Failed",
            _                     => b.Status
        };
        var subject = $"{subjectPrefix} Ingestion batch {label}: " +
                      $"{b.AcceptedRows}/{b.TotalRows} accepted";

        var html = $"""
            <h2>Campaign Platform — Ingestion Report</h2>
            <p>Project: <strong>{System.Net.WebUtility.HtmlEncode(projectName)}</strong></p>
            <table border="1" cellpadding="6" cellspacing="0">
              <tr><td>Batch</td><td>{b.Id}</td></tr>
              <tr><td>Source</td><td>{System.Net.WebUtility.HtmlEncode(b.SourceType)} — {System.Net.WebUtility.HtmlEncode(b.SourceRef)}</td></tr>
              <tr><td>Outcome</td><td><strong>{label}</strong></td></tr>
              <tr><td>Total rows</td><td>{b.TotalRows}</td></tr>
              <tr style="color:green"><td>Accepted</td><td>{b.AcceptedRows}</td></tr>
              <tr style="color:#c00"><td>Rejected</td><td>{b.RejectedRows}</td></tr>
              <tr><td>Ingested at</td><td>{b.IngestedAt:yyyy-MM-dd HH:mm:ss zzz}</td></tr>
            </table>
            <p style="font-size:smaller;color:#666">This message was generated automatically. Do not reply.</p>
            """;
        var text = $"""
            Campaign Platform — Ingestion Report
            Project: {projectName}
            Batch:   {b.Id}
            Source:  {b.SourceType} — {b.SourceRef}
            Outcome: {label}
            Rows:    {b.AcceptedRows} accepted / {b.RejectedRows} rejected / {b.TotalRows} total
            At:      {b.IngestedAt:O}
            """;
        return (subject, html, text);
    }
}

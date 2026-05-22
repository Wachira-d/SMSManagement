using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMSManagement.Infrastructure.Persistence;
using SMSManagement.Modules.Core.Security;
using SMSManagement.Modules.Shortlink.Services;
using SMSManagement.Modules.Sms.Domain;
using SMSManagement.Modules.Workflow.Domain;

namespace SMSManagement.Modules.Notifications;

/// <summary>Result of building one ingestion round's per-recipient report.</summary>
public sealed record RoundReport(
    byte[] Csv, int Recipients, int Delivered, int Sent, int Failed, int Clicked,
    bool AnyNonFinalSms, bool AnyNonTerminalWorkflow);

public interface IRoundReportService
{
    /// <summary>
    /// Builds the per-recipient CSV report for one ingestion batch — the mapped
    /// source columns joined with the SMS outcome and shortlink click activity.
    /// Returns null when the batch produced no workflow instances.
    /// </summary>
    Task<RoundReport?> BuildAsync(Guid batchId, CancellationToken ct = default);
}

public sealed class RoundReportService : IRoundReportService
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    private readonly AppDbContext _db;
    private readonly FieldEncryptor _crypto;
    private readonly IOptionsMonitor<ShortlinkOptions> _slOpts;

    public RoundReportService(
        AppDbContext db, FieldEncryptor crypto, IOptionsMonitor<ShortlinkOptions> slOpts)
    {
        _db = db;
        _crypto = crypto;
        _slOpts = slOpts;
    }

    private sealed record Sms(
        Guid InstanceId, string Provider, string? SenderId, SmsStatus Status,
        short Attempts, DateTimeOffset CreatedAt, DateTimeOffset? SentAt,
        DateTimeOffset? DeliveredAt, string? ErrorCode, string? ProviderMessageId,
        string? RawProviderResponse, byte[] EncryptedBody);

    private sealed record Click(Guid ShortlinkId, DateTimeOffset ClickedAt);

    private sealed record Row(
        string MaskedPhone, WorkflowState State,
        IReadOnlyDictionary<string, string> Source,
        int SmsCount, string? Provider, string? SenderId, SmsStatus? SmsStatus,
        short Attempts, DateTimeOffset? SentAt, DateTimeOffset? DeliveredAt,
        string? ErrorCode, string? ProviderMessageId, string? ProviderResponse,
        string Body, string? ShortlinkUrl, string? ShortlinkTarget,
        int ClickCount, DateTimeOffset? FirstClickedAt);

    public async Task<RoundReport?> BuildAsync(Guid batchId, CancellationToken ct = default)
    {
        // IgnoreQueryFilters: serves the background notifier (no user) and an
        // already-access-checked controller alike.
        var instances = await _db.WorkflowInstances
            .IgnoreQueryFilters()
            .Where(w => w.IngestionBatchId == batchId)
            .Select(w => new { w.Id, w.State, w.MaskedPhone, w.EncryptedPayload })
            .ToListAsync(ct);
        if (instances.Count == 0) return null;

        var ids = instances.Select(w => w.Id).ToHashSet();
        var anyNonTerminalWf = instances.Any(w => w.State is not (
            WorkflowState.Completed or WorkflowState.Failed or WorkflowState.Expired));

        var sms = await _db.SmsMessages
            .IgnoreQueryFilters()
            .Where(m => m.WorkflowInstanceId != null && ids.Contains(m.WorkflowInstanceId.Value))
            .Select(m => new Sms(
                m.WorkflowInstanceId!.Value, m.Provider, m.SenderId, m.Status, m.Attempts,
                m.CreatedAt, m.SentAt, m.DeliveredAt, m.ErrorCode, m.ProviderMessageId,
                m.RawProviderResponse, m.EncryptedBody))
            .ToListAsync(ct);
        var anyNonFinalSms = sms.Any(m => m.Status is
            SmsStatus.Queued or SmsStatus.Sending or SmsStatus.Sent);

        var shortlinks = await _db.Shortlinks
            .IgnoreQueryFilters()
            .Where(s => s.WorkflowInstanceId != null && ids.Contains(s.WorkflowInstanceId.Value))
            .Select(s => new { s.Id, s.WorkflowInstanceId, s.Slug, s.EncryptedTargetUrl, s.ClickCount })
            .ToListAsync(ct);
        var slIds = shortlinks.Select(s => s.Id).ToList();
        var clicks = slIds.Count == 0
            ? new List<Click>()
            : await _db.ShortlinkClicks
                .Where(c => slIds.Contains(c.ShortlinkId))
                .Select(c => new Click(c.ShortlinkId, c.ClickedAt))
                .ToListAsync(ct);

        var projBase = await (
            from b in _db.IngestionBatches.IgnoreQueryFilters()
            where b.Id == batchId
            join p in _db.Projects.IgnoreQueryFilters() on b.ProjectId equals p.Id
            select p.ShortlinkBaseUrl).FirstOrDefaultAsync(ct);
        var baseUrl = (string.IsNullOrWhiteSpace(projBase)
            ? _slOpts.CurrentValue.PublicBaseUrl ?? string.Empty
            : projBase).TrimEnd('/');

        var smsByInstance = sms.GroupBy(m => m.InstanceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).ToList());
        var slByInstance = shortlinks
            .Where(s => s.WorkflowInstanceId != null)
            .GroupBy(s => s.WorkflowInstanceId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var firstClickBySl = clicks.GroupBy(c => c.ShortlinkId)
            .ToDictionary(g => g.Key, g => g.Min(c => c.ClickedAt));

        var rows = new List<Row>(instances.Count);
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

            rows.Add(new Row(
                inst.MaskedPhone, inst.State, src,
                msgs?.Count ?? 0,
                latest?.Provider, latest?.SenderId, latest?.Status,
                latest?.Attempts ?? 0, latest?.SentAt, latest?.DeliveredAt,
                latest?.ErrorCode, latest?.ProviderMessageId, latest?.RawProviderResponse,
                latest is null ? string.Empty : Decrypt(latest.EncryptedBody),
                slUrl, sl is null ? null : Decrypt(sl.EncryptedTargetUrl),
                sl?.ClickCount ?? 0, firstClick));
        }

        return new RoundReport(
            BuildCsv(rows),
            rows.Count,
            sms.Count(m => m.Status == SmsStatus.Delivered),
            sms.Count(m => m.Status == SmsStatus.Sent),
            sms.Count(m => m.Status is SmsStatus.Failed or SmsStatus.Rejected or SmsStatus.Expired),
            rows.Count(r => r.ClickCount > 0),
            anyNonFinalSms,
            anyNonTerminalWf);
    }

    /// <summary>Per-recipient CSV: the dynamic mapped source columns followed
    /// by the campaign outcome — shortlink, clicks, send + delivery status.</summary>
    private byte[] BuildCsv(IReadOnlyList<Row> rows)
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
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }
}

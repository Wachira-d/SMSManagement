using SMSManagement.Modules.Reporting.Domain;

namespace SMSManagement.Modules.Reporting.Services;

public interface IReportingService
{
    Task<IReadOnlyList<SmsCampaignRow>> SmsCampaignAsync(
        Guid projectId, DateRange range, CancellationToken ct = default);

    Task<IReadOnlyList<ProviderPerformanceRow>> ProviderPerformanceAsync(
        DateRange range, CancellationToken ct = default);

    Task<DeliveryFunnelRow> DeliveryFunnelAsync(
        Guid projectId, DateRange range, CancellationToken ct = default);

    Task<IReadOnlyList<ShortlinkRow>> ShortlinkPerformanceAsync(
        Guid projectId, DateRange range, int top, CancellationToken ct = default);

    Task<IReadOnlyList<ReminderRow>> ReminderEffectivenessAsync(
        Guid projectId, CancellationToken ct = default);

    Task<IReadOnlyList<IngestionQualityRow>> IngestionQualityAsync(
        Guid projectId, DateRange range, CancellationToken ct = default);

    Task<IReadOnlyList<AuditRow>> AuditTrailAsync(
        Guid projectId, DateRange range, int take, CancellationToken ct = default);

    /// <summary>Stream any of the above as a CSV for download.</summary>
    Task ExportCsvAsync<TRow>(IEnumerable<TRow> rows, Stream destination, CancellationToken ct = default);
}

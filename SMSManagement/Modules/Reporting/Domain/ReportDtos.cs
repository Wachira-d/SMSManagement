namespace SMSManagement.Modules.Reporting.Domain;

public sealed record DateRange(DateTimeOffset From, DateTimeOffset To);

// ---------- 1. SMS dispatch summary ----------
public sealed record SmsCampaignRow(
    Guid ProjectId,
    string Provider,
    DateOnly Date,
    int Queued,
    int Sent,
    int Delivered,
    int Failed,
    int Rejected,
    double DeliveryRate,
    double FailureRate);

// ---------- 2. Per-provider performance ----------
public sealed record ProviderPerformanceRow(
    string Provider,
    int Volume,
    double DeliveryRate,
    double AverageAttempts,
    int CircuitTrips,
    TimeSpan AverageDispatchLatency);

// ---------- 3. Delivery funnel ----------
public sealed record DeliveryFunnelRow(
    Guid ProjectId,
    int Targeted,
    int Sent,
    int Delivered,
    int Clicked,
    int Converted,
    double SentRate,
    double DeliveryRate,
    double ClickThroughRate,
    double ConversionRate);

// ---------- 4. Shortlink performance ----------
public sealed record ShortlinkRow(
    Guid ShortlinkId,
    string Slug,
    int Clicks,
    int UniqueIpHashes,
    DateTimeOffset? FirstClickAt,
    DateTimeOffset? LastClickAt,
    string TopDevice,
    string TopCountry);

// ---------- 5. Reminder effectiveness ----------
public sealed record ReminderRow(
    Guid DefinitionId,
    int Reminder0Sent,
    int Reminder1Sent,
    int Reminder2Sent,
    int Reminder3PlusSent,
    int ConvertedAt0,
    int ConvertedAt1,
    int ConvertedAt2,
    int ConvertedAt3Plus,
    int Expired);

// ---------- 6. Ingestion quality ----------
public sealed record IngestionQualityRow(
    Guid BatchId,
    string SourceType,
    string SourceRef,
    int TotalRows,
    int AcceptedRows,
    int RejectedRows,
    double AcceptanceRate,
    string Status,
    DateTimeOffset IngestedAt);

// ---------- 7. Audit Trail ----------
public sealed record AuditRow(
    DateTimeOffset At,
    Guid UserId,
    string? UserEmail,
    string Action,
    string EntityType,
    string EntityId,
    string? IpAddress,
    string? CorrelationId);

// ---------- 8. User Activity ----------
public sealed record UserActivityRow(
    Guid UserId,
    string? Email,
    string? DisplayName,
    int TotalActions,
    int Logins,
    int SmsDispatchInitiated,
    int IngestionUploads,
    int ProjectsCreated,
    DateTimeOffset? LastActiveAt);

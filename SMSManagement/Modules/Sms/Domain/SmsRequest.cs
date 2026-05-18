namespace SMSManagement.Modules.Sms.Domain;

/// <summary>Inbound contract — what a caller (workflow / API) hands to the dispatcher.</summary>
public sealed record SmsRequest(
    Guid ProjectId,
    string Recipient,
    string Body,
    string? SenderId,
    SmsPriority Priority,
    DateTimeOffset? ScheduledFor,
    Guid? WorkflowInstanceId,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>What an SMS provider returns after attempting dispatch.</summary>
public sealed record ProviderDispatchResult(
    bool Success,
    string? ProviderMessageId,
    string? ErrorCode,
    string? RawResponse);

namespace SMSManagement.Modules.Notifications.Relay;

/// <summary>
/// Maps relay status strings to <see cref="OutboundEmailStatus"/> and applies
/// a status update to a row under the rules both the webhook and the pull
/// reconciler must agree on.
/// </summary>
public static class RelayStatus
{
    /// <summary>The relay's wire forms (EmailModels.cs). Null for anything we
    /// don't recognise, so an unexpected value is reported rather than guessed.</summary>
    public static OutboundEmailStatus? Parse(string? wire) => wire?.Trim().ToLowerInvariant() switch
    {
        "queued"    => OutboundEmailStatus.Queued,
        "scheduled" => OutboundEmailStatus.Scheduled,
        "sending"   => OutboundEmailStatus.Sending,
        "sent"      => OutboundEmailStatus.Sent,
        "failed"    => OutboundEmailStatus.Failed,
        "cancelled" => OutboundEmailStatus.Cancelled,
        _ => null
    };

    public sealed record Update(
        OutboundEmailStatus Status,
        int? Attempts = null,
        string? ProviderMessageId = null,
        string? Error = null,
        DateTimeOffset? SentAt = null);

    /// <summary>
    /// Applies <paramref name="u"/> to <paramref name="row"/> and returns the
    /// outcome: <c>applied</c>, <c>duplicate</c> or <c>ignored-stale</c>.
    ///
    /// Sent is the one terminal state that nothing overrides. The relay can
    /// legitimately move a message failed → queued → sent (an admin requeue),
    /// so Failed is not terminal; but a late or replayed "failed", or a pull
    /// that read "queued" before the "sent" webhook landed, must not undo a
    /// delivery we already know happened.
    /// </summary>
    public static string Apply(OutboundEmail row, Update u, string source, DateTimeOffset now)
    {
        if (row.Status == OutboundEmailStatus.Sent && u.Status != OutboundEmailStatus.Sent)
            return "ignored-stale";

        if (row.Status == u.Status)
        {
            // Same status again — a webhook retry or a pull that found nothing
            // new. Still fill in details an earlier report lacked.
            row.ProviderMessageId ??= u.ProviderMessageId;
            row.SentAt ??= u.SentAt;
            if (u.Attempts is { } a && a > row.Attempts) row.Attempts = a;
            return "duplicate";
        }

        row.Status = u.Status;
        row.StatusSource = source;
        row.StatusUpdatedAt = now;
        if (u.Attempts is { } attempts) row.Attempts = attempts;
        if (u.ProviderMessageId is not null) row.ProviderMessageId = u.ProviderMessageId;

        if (u.Status == OutboundEmailStatus.Sent)
        {
            row.SentAt = u.SentAt ?? now;
            row.LastError = null;   // a requeued message that finally went out
        }
        else if (u.Error is not null)
        {
            row.LastError = u.Error;
        }
        return "applied";
    }
}

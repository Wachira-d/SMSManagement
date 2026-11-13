namespace SMSManagement.Modules.Notifications;

/// <summary>What a transport reports back about one send attempt.</summary>
/// <param name="Status">Where the message stands after the call. SMTP answers
/// Sent or Failed straight away; the relay usually answers Queued and settles
/// later through its webhook.</param>
/// <param name="RelayMessageId">The relay's id for the message, when there is one.</param>
/// <param name="Error">Human-readable reason when the send didn't succeed.</param>
public sealed record EmailSendResult(
    OutboundEmailStatus Status,
    string? RelayMessageId = null,
    string? Error = null,
    DateTimeOffset? SentAt = null)
{
    public static EmailSendResult Skip(string why) => new(OutboundEmailStatus.Skipped, Error: why);
}

/// <summary>
/// One way of actually putting an email on the wire. Unlike
/// <see cref="IEmailSender"/>, a transport reports what happened — the
/// dispatcher records it — but must still never throw for an ordinary
/// delivery failure.
/// </summary>
public interface IEmailTransport
{
    /// <summary>Matches <see cref="EmailOptions.Provider"/>.</summary>
    string Name { get; }

    /// <param name="idempotencyKey">Stable per logical message, so a retry of
    /// the same send can be recognised downstream.</param>
    Task<EmailSendResult> SendAsync(EmailMessage message, string idempotencyKey, CancellationToken ct);
}

using SMSManagement.Modules.Shortlink.Domain;

namespace SMSManagement.Modules.Shortlink.Abuse;

public interface IShortlinkAbuseTracker
{
    /// <summary>Salts + SHA-256 the client IP using the shared IP-hash salt.
    /// Output is the canonical byte form persisted in BlockedIp.IpHash.</summary>
    byte[] HashIp(string ip);

    /// <summary>Returns the active BlockedIp row if this hash is currently
    /// blocked (BlockedUntil > now AND not manually unblocked). Cheap — used
    /// on the redirect hot path.</summary>
    Task<BlockedIp?> GetActiveBlockAsync(byte[] ipHash, CancellationToken ct = default);

    /// <summary>Record a failure. If the cumulative failures in the window
    /// reach the threshold, atomically create a BlockedIp row and return it.</summary>
    Task<BlockedIp?> RecordFailureAsync(
        byte[] ipHash, string reason, string? slug, CancellationToken ct = default);

    /// <summary>Admin action — clears the block and records who/why.</summary>
    Task UnblockAsync(Guid blockId, Guid actorUserId, string reason, CancellationToken ct = default);
}

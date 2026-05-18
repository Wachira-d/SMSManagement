using System.Security.Cryptography;
using System.Text;

namespace SMSManagement.Modules.Sms.Webhooks;

/// <summary>
/// HMAC-SHA-256 verification for inbound provider webhooks (delivery receipts).
/// Providers usually sign the raw body with a shared secret and include the
/// digest in a header. We:
///   - reject if the signature header is missing or doesn't match
///   - reject if the timestamp is too old (replay window: 5 min default)
///   - use constant-time compare
/// </summary>
public static class WebhookHmac
{
    public static bool Verify(
        ReadOnlySpan<byte> body,
        string secretBase64,
        string? signatureHex,
        string? timestampUnixSeconds,
        TimeProvider clock,
        int allowedSkewSeconds = 300)
    {
        if (string.IsNullOrEmpty(signatureHex) || string.IsNullOrEmpty(timestampUnixSeconds))
            return false;

        if (!long.TryParse(timestampUnixSeconds, out var ts)) return false;
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - ts) > allowedSkewSeconds) return false;

        byte[] key;
        try { key = Convert.FromBase64String(secretBase64); }
        catch { return false; }

        // Include timestamp in the signed material so a captured signature
        // can't be replayed against a different body.
        var prefix = Encoding.UTF8.GetBytes($"{ts}.");
        var combined = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
        body.CopyTo(combined.AsSpan(prefix.Length));

        var expected = HMACSHA256.HashData(key, combined);
        byte[] actual;
        try { actual = Convert.FromHexString(signatureHex); }
        catch { return false; }

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

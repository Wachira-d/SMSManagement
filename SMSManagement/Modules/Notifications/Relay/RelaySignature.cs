using System.Security.Cryptography;
using System.Text;

namespace SMSManagement.Modules.Notifications.Relay;

/// <summary>
/// HMAC-SHA256 signatures in EmailRelayAPI's exact formats. Both secrets are
/// used as their raw UTF-8 bytes — the relay never base64- or hex-decodes
/// them, so neither may we. Hex output is lower-case with a <c>sha256=</c>
/// prefix, matching what the relay emits and accepts.
/// </summary>
public static class RelaySignature
{
    public const string Prefix = "sha256=";

    /// <summary>Request signing, for a relay client with "Require signed
    /// requests" on (RequestSignatureVerifier.cs):
    /// <c>HMAC(secret, "{ts}.{METHOD}.{path}.{sha256hex(body)}")</c>.</summary>
    /// <param name="path">The API path as the relay's ASP.NET pipeline sees it —
    /// <c>/api/v1/emails</c>, no query string, and without any IIS virtual
    /// directory, which lands in PathBase rather than Path.</param>
    public static string SignRequest(string secret, long unixSeconds, string method, string path, ReadOnlySpan<byte> body)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = $"{unixSeconds}.{method.ToUpperInvariant()}.{path}.{bodyHash}";
        return Prefix + Hmac(secret, Encoding.UTF8.GetBytes(canonical));
    }

    /// <summary>Webhook signature (WebhookNotifier.cs):
    /// <c>HMAC(secret, ts + "." + body)</c>, over the exact bytes posted.
    /// Computed from the raw body rather than a re-serialised object so a
    /// whitespace or key-order difference can't break verification.</summary>
    public static string ComputeWebhook(string secret, string timestamp, ReadOnlySpan<byte> rawBody)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var signed = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(signed, 0);
        rawBody.CopyTo(signed.AsSpan(prefix.Length));
        return Prefix + Hmac(secret, signed);
    }

    /// <summary>Constant-time comparison of a received <c>X-Relay-Signature</c>
    /// against the expected value.</summary>
    public static bool VerifyWebhook(string secret, string timestamp, ReadOnlySpan<byte> rawBody, string? received)
    {
        if (string.IsNullOrWhiteSpace(received)) return false;
        var expected = ComputeWebhook(secret, timestamp, rawBody);
        // Header values arrive trimmed by Kestrel, but a proxy may re-case hex.
        var supplied = received.Trim().ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(supplied));
    }

    private static string Hmac(string secret, byte[] data)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data)).ToLowerInvariant();
}

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using SMSManagement.Modules.Sms.Webhooks;

namespace SMSManagement.Tests.Sms;

public sealed class WebhookHmacTests
{
    private static readonly string SecretB64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly TimeProvider _clock = TimeProvider.System;

    private static string Sign(string secretB64, long ts, ReadOnlySpan<byte> body)
    {
        var key = Convert.FromBase64String(secretB64);
        var prefix = Encoding.UTF8.GetBytes($"{ts}.");
        var combined = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
        body.CopyTo(combined.AsSpan(prefix.Length));
        return Convert.ToHexString(HMACSHA256.HashData(key, combined));
    }

    [Fact]
    public void Verifies_correctly_signed_request()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = Encoding.UTF8.GetBytes("""{"messageId":"abc","status":"DELIVERED"}""");
        var sig = Sign(SecretB64, ts, body);
        WebhookHmac.Verify(body, SecretB64, sig, ts.ToString(), _clock).Should().BeTrue();
    }

    [Fact]
    public void Rejects_tampered_body()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = Encoding.UTF8.GetBytes("""{"status":"DELIVERED"}""");
        var sig = Sign(SecretB64, ts, body);
        var tampered = Encoding.UTF8.GetBytes("""{"status":"FAILED"}""");
        WebhookHmac.Verify(tampered, SecretB64, sig, ts.ToString(), _clock).Should().BeFalse();
    }

    [Fact]
    public void Rejects_old_timestamp_outside_skew()
    {
        var ts = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds();
        var body = Encoding.UTF8.GetBytes("x");
        var sig = Sign(SecretB64, ts, body);
        WebhookHmac.Verify(body, SecretB64, sig, ts.ToString(), _clock).Should().BeFalse();
    }

    [Fact]
    public void Rejects_missing_headers()
    {
        var body = Encoding.UTF8.GetBytes("x");
        WebhookHmac.Verify(body, SecretB64, null,      "123", _clock).Should().BeFalse();
        WebhookHmac.Verify(body, SecretB64, "abc",     null,  _clock).Should().BeFalse();
        WebhookHmac.Verify(body, SecretB64, "abc",     "not-a-number", _clock).Should().BeFalse();
    }

    [Fact]
    public void Rejects_wrong_secret()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = Encoding.UTF8.GetBytes("x");
        var sig = Sign(SecretB64, ts, body);
        var wrongSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        WebhookHmac.Verify(body, wrongSecret, sig, ts.ToString(), _clock).Should().BeFalse();
    }
}

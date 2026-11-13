using System.Text;
using FluentAssertions;
using SMSManagement.Modules.Notifications.Relay;

namespace SMSManagement.Tests.Notifications;

/// <summary>
/// Expected values were computed independently with Python's hmac/hashlib
/// from the relay's own formulas (WebhookNotifier.cs, RequestSignatureVerifier.cs),
/// so these pin wire compatibility, not just self-consistency.
/// </summary>
public sealed class RelaySignatureTests
{
    private static readonly byte[] WebhookBody =
        Encoding.UTF8.GetBytes("{\"event\":\"email.sent\",\"subject\":\"ทดสอบ\"}");

    [Fact]
    public void Webhook_signature_matches_relay_formula()
        => RelaySignature.ComputeWebhook("whsec-test", "1700000000", WebhookBody)
            .Should().Be("sha256=8017809f7706fcf170c9ab78226f1b21f1bbc1d1fec9b9cac60d866fb18fe768");

    [Fact]
    public void Request_signature_matches_relay_formula_for_post()
        => RelaySignature.SignRequest("sign-test", 1700000000, "post", "/api/v1/emails",
                Encoding.UTF8.GetBytes("{\"to\":[]}"))
            .Should().Be("sha256=0d06539fbaac422de5e851244d32f3d23357d2540d0b8d7b24d2b35c50219273");

    [Fact]
    public void Request_signature_hashes_empty_body_for_get()
        => RelaySignature.SignRequest("sign-test", 1700000000, "GET", "/api/v1/me", ReadOnlySpan<byte>.Empty)
            .Should().Be("sha256=d0b7b5a2b12a038597db3c88e2804c9bbc1b4573b44b3c9fd616799fcb15813c");

    [Fact]
    public void Verify_accepts_valid_and_uppercase_hex()
    {
        var sig = RelaySignature.ComputeWebhook("whsec-test", "1700000000", WebhookBody);
        RelaySignature.VerifyWebhook("whsec-test", "1700000000", WebhookBody, sig).Should().BeTrue();
        RelaySignature.VerifyWebhook("whsec-test", "1700000000", WebhookBody,
            "sha256=" + sig["sha256=".Length..].ToUpperInvariant()).Should().BeTrue();
    }

    [Theory]
    [InlineData("wrong-secret", "1700000000")]
    [InlineData("whsec-test", "1700000001")]
    public void Verify_rejects_wrong_secret_or_timestamp(string secret, string ts)
    {
        var sig = RelaySignature.ComputeWebhook("whsec-test", "1700000000", WebhookBody);
        RelaySignature.VerifyWebhook(secret, ts, WebhookBody, sig).Should().BeFalse();
    }

    [Fact]
    public void Verify_rejects_tampered_body_and_garbage()
    {
        var sig = RelaySignature.ComputeWebhook("whsec-test", "1700000000", WebhookBody);
        var tampered = Encoding.UTF8.GetBytes("{\"event\":\"email.sent\",\"subject\":\"ทดสอบ \"}");
        RelaySignature.VerifyWebhook("whsec-test", "1700000000", tampered, sig).Should().BeFalse();
        RelaySignature.VerifyWebhook("whsec-test", "1700000000", WebhookBody, "sha256=abc").Should().BeFalse();
        RelaySignature.VerifyWebhook("whsec-test", "1700000000", WebhookBody, null).Should().BeFalse();
    }
}

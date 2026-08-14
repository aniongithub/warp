using System.Security.Cryptography;
using System.Text;
using Stripe;

namespace Warp.Latinum.Tests;

/// <summary>
/// Unit coverage for the post-#29 Stripe webhook signature-verification decision. warp.latinum's
/// StripeWebhookController calls <see cref="EventUtility.ConstructEvent(string, string, string, long, bool)"/>
/// to reject forged webhooks. This is pure Stripe.net crypto (HMAC-SHA256), so it is tested directly
/// with no network or HTTP plumbing: a correctly-signed payload is accepted, and tampered payloads or
/// a wrong signing secret are rejected.
/// </summary>
public class StripeSignatureVerificationTests
{
    private const string Secret = "whsec_test_0123456789abcdef0123456789abcdef";

    private const string Payload =
        "{\"id\":\"evt_test_123\",\"object\":\"event\",\"api_version\":\"2022-11-15\",\"created\":1680000000," +
        "\"request\":null,\"type\":\"payment_intent.succeeded\"," +
        "\"data\":{\"object\":{\"id\":\"pi_1\",\"object\":\"payment_intent\",\"amount\":1000,\"currency\":\"usd\",\"status\":\"succeeded\"}}}";

    private static string SignatureHeader(string payload, string secret, long? timestamp = null)
    {
        var t = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{t}.{payload}"));
        var signature = Convert.ToHexString(hash).ToLowerInvariant();
        return $"t={t},v1={signature}";
    }

    [Fact]
    public void CorrectlySignedPayload_IsAccepted()
    {
        var header = SignatureHeader(Payload, Secret);

        var evt = EventUtility.ConstructEvent(Payload, header, Secret, tolerance: 300, throwOnApiVersionMismatch: false);

        evt.Id.Should().Be("evt_test_123");
        evt.Type.Should().Be("payment_intent.succeeded");
    }

    [Fact]
    public void TamperedPayload_IsRejected()
    {
        // Sign the original payload, then deliver a modified body under the same signature.
        var header = SignatureHeader(Payload, Secret);
        var tampered = Payload.Replace("pi_1", "pi_ATTACKER");

        var act = () => EventUtility.ConstructEvent(tampered, header, Secret, tolerance: 300, throwOnApiVersionMismatch: false);

        act.Should().Throw<StripeException>();
    }

    [Fact]
    public void WrongSecret_IsRejected()
    {
        // Signature computed with a different secret than the one used to verify.
        var header = SignatureHeader(Payload, "whsec_attacker_key_ffffffffffffffffffffffff");

        var act = () => EventUtility.ConstructEvent(Payload, header, Secret, tolerance: 300, throwOnApiVersionMismatch: false);

        act.Should().Throw<StripeException>();
    }

    [Fact]
    public void ExpiredTimestamp_OutsideTolerance_IsRejected()
    {
        // A signature far outside the tolerance window is treated as a replay and rejected.
        var oldTs = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var header = SignatureHeader(Payload, Secret, oldTs);

        var act = () => EventUtility.ConstructEvent(Payload, header, Secret, tolerance: 300, throwOnApiVersionMismatch: false);

        act.Should().Throw<StripeException>();
    }
}

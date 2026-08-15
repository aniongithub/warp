using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Warp.E2E.Tests;

/// <summary>
/// Builds and signs Stripe webhook events exactly the way localstripe / real Stripe do, so the
/// gateway's <c>Stripe.EventUtility.ConstructEvent</c> verification accepts them. The signature is
/// <c>t=&lt;ts&gt;,v1=HMAC-SHA256(secret, "&lt;ts&gt;.&lt;rawBody&gt;")</c> and MUST be computed over the exact
/// bytes that are POSTed (no re-serialization in between).
/// </summary>
public static class StripeWebhooks
{
    /// <summary>Computes a Stripe-Signature header value for the given raw body + secret.</summary>
    public static string Sign(string secret, string rawBody, DateTimeOffset? at = null)
    {
        var ts = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var signedPayload = $"{ts}.{rawBody}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"t={ts},v1={hex}";
    }

    /// <summary>Raw JSON for a <c>payment_intent.succeeded</c> event carrying the given PI id.</summary>
    public static string PaymentIntentSucceeded(string paymentIntentId, long amount = 500)
        => BuildEvent("payment_intent.succeeded", new Dictionary<string, object?>
        {
            ["id"] = paymentIntentId,
            ["object"] = "payment_intent",
            ["amount"] = amount,
            ["currency"] = "usd",
            ["status"] = "succeeded",
        });

    /// <summary>Raw JSON for a <c>checkout.session.completed</c> event carrying the given session id.</summary>
    public static string CheckoutSessionCompleted(string sessionId, string mode = "subscription")
        => BuildEvent("checkout.session.completed", new Dictionary<string, object?>
        {
            ["id"] = sessionId,
            ["object"] = "checkout.session",
            ["mode"] = mode,
            ["status"] = "complete",
            ["payment_status"] = "paid",
        });

    /// <summary>
    /// Serializes a Stripe-shaped event envelope. The top-level <c>request</c>/<c>livemode</c>/
    /// <c>pending_webhooks</c> fields are required by the Stripe.net 47 EventConverter.
    /// </summary>
    private static string BuildEvent(string type, Dictionary<string, object?> dataObject)
    {
        var evt = new Dictionary<string, object?>
        {
            ["id"] = $"evt_{Guid.NewGuid():N}",
            ["object"] = "event",
            ["api_version"] = "2020-08-27",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["type"] = type,
            ["livemode"] = false,
            ["pending_webhooks"] = 1,
            ["request"] = new Dictionary<string, object?>
            {
                ["id"] = (string?)null,
                ["idempotency_key"] = (string?)null,
            },
            ["data"] = new Dictionary<string, object?>
            {
                ["object"] = dataObject,
            },
        };

        return JsonSerializer.Serialize(evt);
    }
}

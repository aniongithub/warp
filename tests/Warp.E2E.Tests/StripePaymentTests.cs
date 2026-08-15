using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Warp.Core.Data;

namespace Warp.E2E.Tests;

/// <summary>
/// Scenarios 6, 8 and 9: Stripe payment credit via a signed webhook, idempotent redelivery
/// (exactly one grant), and fail-closed rejection of bogus/missing signatures.
/// </summary>
[Trait("Category", "E2E")]
[Collection("e2e")]
public class StripePaymentTests
{
    private readonly E2EStack _stack;

    public StripePaymentTests(E2EStack stack) => _stack = stack;

    // --- Scenario 6 + 8: signed payment webhook grants quota; redelivery grants exactly once ---
    [Fact]
    public async Task Signed_payment_webhook_grants_quota_and_is_idempotent()
    {
        var email = $"pay-{Guid.NewGuid():N}@e2e.test";
        const int amount = 5;
        const int expectedIncrease = amount * 1000; // CurrencyMultiplier = 1000

        // 1. Drive a payment through the gateway to create a PaymentIntent in localstripe + a job.
        var paymentIntentId = await SubmitPaymentAsync(email, amount);
        paymentIntentId.Should().NotBeNullOrEmpty();

        // Before any webhook the quota does not exist yet (the payment route reserves nothing).
        QuotaLimit(email, "slips").Should().BeNull();

        // 2. Deliver a SIGNED payment_intent.succeeded event to latinum.
        var rawBody = StripeWebhooks.PaymentIntentSucceeded(paymentIntentId!);
        using var first = await PostWebhookAsync("/stripe/payment", rawBody,
            StripeWebhooks.Sign(E2EStack.WebhookSecret, rawBody));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadAsStringAsync()).Should().Contain("completed");

        // Quota was granted exactly the expected amount.
        QuotaLimit(email, "slips").Should().Be(expectedIncrease);

        // 3. Re-deliver the SAME signed event (at-least-once). The job-status CAS gates it so the
        //    grant happens exactly once.
        using var second = await PostWebhookAsync("/stripe/payment", rawBody,
            StripeWebhooks.Sign(E2EStack.WebhookSecret, rawBody));

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadAsStringAsync()).Should().Contain("already_processed");

        // Still exactly one grant.
        QuotaLimit(email, "slips").Should().Be(expectedIncrease);
    }

    // --- Scenario 9: fail-closed on a bogus or missing signature (no quota change) ---
    [Fact]
    public async Task Bogus_or_missing_signature_is_rejected_and_grants_nothing()
    {
        var email = $"neg-{Guid.NewGuid():N}@e2e.test";
        var paymentIntentId = await SubmitPaymentAsync(email, 7);
        var rawBody = StripeWebhooks.PaymentIntentSucceeded(paymentIntentId!);

        // Bogus signature -> 400.
        using var bogus = await PostWebhookAsync("/stripe/payment", rawBody, "t=123,v1=deadbeef");
        bogus.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Missing signature header -> 400.
        using var missing = await PostWebhookAsync("/stripe/payment", rawBody, signature: null);
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Fail closed: nothing was granted.
        QuotaLimit(email, "slips").Should().BeNull();
    }

    private async Task<string?> SubmitPaymentAsync(string email, int amount)
    {
        using var client = _stack.NewGatewayClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/payment/submit")
        {
            Content = new StringContent($"{{\"amount\":{amount}}}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-JWT-Email", email);
        using var res = await client.SendAsync(req);
        ((int)res.StatusCode).Should().BeInRange(200, 299,
            $"payment submit should succeed (body: {await res.Content.ReadAsStringAsync()})");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (var name in new[] { "payment_intent_id", "paymentIntentId", "job_id", "jobId" })
            if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(string path, string rawBody, string? signature)
    {
        using var client = _stack.NewLatinumClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json"),
        };
        if (signature != null)
            req.Headers.Add("Stripe-Signature", signature);
        return await client.SendAsync(req);
    }

    private float? QuotaLimit(string key, string quotaName)
    {
        var dc = _stack.NewDataContext();
        try
        {
            var quota = dc.Quotas.FirstOrDefault(q => q.Key == key && q.QuotaName == quotaName);
            return quota?.Limit;
        }
        finally
        {
            (dc as IDisposable)?.Dispose();
        }
    }
}

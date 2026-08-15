using System.Net;
using System.Text;
using Warp.Core.Data;
using Warp.Core.Data.Contexts;
using Warp.Core.Job;

namespace Warp.E2E.Tests;

/// <summary>
/// Scenario 7: a subscription checkout completed via a signed <c>checkout.session.completed</c>
/// webhook provisions the user's plan quota (named after the plan id, postpaid), and a redelivery
/// of the same signed event is idempotent (the job-status guard grants exactly once).
///
/// The gateway's subscription <em>submit</em> path (which asks localstripe to create a Checkout
/// Session) is deliberately NOT exercised here: the pinned localstripe build rejects the
/// checkout-session parameters the production middleware sends (<c>customer_email</c>,
/// <c>expires_at</c>, <c>payment_method_types</c>) with a 400, so a session cannot be created
/// against the stub. The webhook contract is what actually provisions quota, so we enqueue the
/// subscription job exactly as the gateway middleware would (checkout session id == job id, plan
/// in job parameters, identity header on the job) and then deliver the signed webhook. This proves
/// signature verification, job lookup, plan-quota provisioning and idempotency end to end against
/// the real latinum controller; only the stub-incompatible session-create call is skipped.
/// </summary>
[Trait("Category", "E2E")]
[Collection("e2e")]
public class StripeSubscriptionTests
{
    private const string SubscriptionChannel = "stripe_subscription_async";

    private readonly E2EStack _stack;

    public StripeSubscriptionTests(E2EStack stack) => _stack = stack;

    [Fact]
    public async Task Signed_subscription_webhook_provisions_plan_quota_and_is_idempotent()
    {
        var email = $"sub-{Guid.NewGuid():N}@e2e.test";
        const string plan = "basic";
        var sessionId = $"cs_test_e2e_{Guid.NewGuid():N}";

        // Enqueue the subscription job exactly as the gateway middleware would (job id == checkout
        // session id, plan + identity carried on the job) so the webhook handler can resolve it.
        await EnqueueSubscriptionJobAsync(sessionId, email, plan);

        // 1. Deliver a SIGNED checkout.session.completed event to latinum.
        var rawBody = StripeWebhooks.CheckoutSessionCompleted(sessionId);
        var signature = StripeWebhooks.Sign(E2EStack.WebhookSecret, rawBody);

        using (var res = await PostWebhookAsync("/stripe/subscription", rawBody, signature))
        {
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await res.Content.ReadAsStringAsync();
            body.Should().Contain("completed");
            body.Should().Contain(plan);
        }

        // 2. The user now has a postpaid quota named after the plan.
        var quota = Quota(email, plan);
        quota.Should().NotBeNull("the subscription webhook should provision the plan quota");
        quota!.Type.Should().Be("postpaid");

        // 3. Redelivering the SAME signed event is idempotent: the job is now Completed, so the
        //    handler acknowledges without provisioning again. Exactly one plan quota must exist.
        using (var redelivery = await PostWebhookAsync("/stripe/subscription", rawBody, signature))
        {
            redelivery.StatusCode.Should().Be(HttpStatusCode.OK);
            (await redelivery.Content.ReadAsStringAsync()).Should().Contain("already_processed");
        }

        QuotaCount(email, plan).Should().Be(1, "redelivery must not create a second plan quota");
    }

    private async Task EnqueueSubscriptionJobAsync(string sessionId, string email, string plan)
    {
        var ctx = _stack.NewJobContext(SubscriptionChannel);
        var job = new Job
        {
            Id = sessionId,
            Status = JobStatus.Queued,
            User = new SqliteDataContext.User { Email = email },
            Headers = new Dictionary<string, string> { ["X-JWT-Email"] = email },
            Parameters = new Dictionary<string, object?>
            {
                ["type"] = "stripe_subscription",
                ["subscription_plan"] = plan,
                ["session_id"] = sessionId,
            },
        };
        await ctx.EnqueueJobAsync(job);
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(string path, string rawBody, string signature)
    {
        using var client = _stack.NewLatinumClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Stripe-Signature", signature);
        return await client.SendAsync(req);
    }

    private IQuota? Quota(string key, string quotaName)
    {
        var dc = _stack.NewDataContext();
        try
        {
            return dc.Quotas.FirstOrDefault(q => q.Key == key && q.QuotaName == quotaName);
        }
        finally
        {
            (dc as IDisposable)?.Dispose();
        }
    }

    private int QuotaCount(string key, string quotaName)
    {
        var dc = _stack.NewDataContext();
        try
        {
            return dc.Quotas.Count(q => q.Key == key && q.QuotaName == quotaName);
        }
        finally
        {
            (dc as IDisposable)?.Dispose();
        }
    }
}

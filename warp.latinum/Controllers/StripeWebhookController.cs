using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Warp.Core.Data;
using Warp.Core.Extensions;
using Warp.Core.Job;
using Warp.Core.Job.Contexts;
using Warp.Latinum.Attributes;
using Warp.Latinum.Middleware.Stripe;

namespace Warp.Latinum.Controllers;

/// <summary>
/// Options for the Stripe webhook controller. Bound from the controller's <c>Options</c> configuration
/// section (see <c>ControllerExtensions.AddControllersFromConfig</c>). Controls signature verification
/// of inbound Stripe webhooks.
/// </summary>
public class StripeWebhookOptions
{
    /// <summary>
    /// When true (default), inbound webhooks must carry a valid <c>Stripe-Signature</c> that verifies
    /// against the configured signing secret. Requests that fail verification are rejected with 400.
    /// </summary>
    public bool VerifySignature { get; set; } = true;

    /// <summary>
    /// EXPLICIT dev/test bypass. When true, signature verification is skipped entirely. Off by default
    /// and must never be enabled in production.
    /// </summary>
    public bool AllowUnverifiedWebhooksInsecure { get; set; } = false;

    /// <summary>Shared/default webhook signing secret (e.g. from <c>${STRIPE_WEBHOOK_SECRET:}</c>).</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Signing secret for the payment webhook endpoint. Falls back to <see cref="WebhookSecret"/>.</summary>
    public string? PaymentWebhookSecret { get; set; }

    /// <summary>Signing secret for the subscription webhook endpoint. Falls back to the payment secret.</summary>
    public string? SubscriptionWebhookSecret { get; set; }

    /// <summary>Allowed timestamp tolerance (seconds) when verifying the Stripe signature.</summary>
    public long SignatureToleranceSeconds { get; set; } = 300;
}

[ApiController]
[Route("/stripe")]
[StripePaymentController(Events = new[] { "payment_intent.succeeded", "payment_intent.payment_failed" })]
[StripeSubscriptionController(Events = new[] { "checkout.session.completed", "customer.subscription.created", "customer.subscription.updated", "customer.subscription.deleted", "invoice.payment_succeeded", "invoice.payment_failed" })]
public class StripeWebhookController : ControllerBase
{
    private readonly IDataContext _dataContext;
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly StripePaymentOptions _options;
    private readonly StripeWebhookOptions _webhookOptions;
    private readonly IJobContext _jobContext;
    private readonly string? _paymentWebhookSecret;
    private readonly string? _subscriptionWebhookSecret;

    public StripeWebhookController(IDataContext dataContext, ILogger<StripeWebhookController> logger, IOptions<StripePaymentOptions> options, IOptions<StripeWebhookOptions> webhookOptions)
    {
        _dataContext = dataContext;
        _logger = logger;
        _options = options.Value;
        _webhookOptions = webhookOptions.Value;

        // Resolve per-endpoint signing secrets. Config values take precedence, then environment
        // variables, then a sensible fallback (subscription falls back to the payment secret).
        _paymentWebhookSecret = FirstNonEmpty(
            _webhookOptions.PaymentWebhookSecret,
            _webhookOptions.WebhookSecret,
            Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET"));
        _subscriptionWebhookSecret = FirstNonEmpty(
            _webhookOptions.SubscriptionWebhookSecret,
            Environment.GetEnvironmentVariable("STRIPE_SUBSCRIPTION_WEBHOOK_SECRET"),
            _paymentWebhookSecret);

        if (_webhookOptions.AllowUnverifiedWebhooksInsecure || !_webhookOptions.VerifySignature)
            _logger.LogWarning("SECURITY: Stripe webhook signature verification is DISABLED. Inbound webhooks will be processed without verifying their signature. Never use this in production.");

        // Initialize JobContext once in constructor.
        // Payment jobs always live in the stripe_payment_async channel (the channel is part
        // of the Redis key), so use it explicitly rather than the unset StripePaymentOptions
        // default. Subscription jobs use their own context (see CreateSubscriptionJobContext).
        var jobContext = new RedisJobContext();
        jobContext.Initialize(_options.ConnectionString, "stripe_payment_async");
        _jobContext = jobContext;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// Reads the raw request body and, unless verification is disabled, validates the Stripe signature
    /// against the provided signing secret. Returns an error result on failure, otherwise the parsed
    /// webhook payload. The raw body (not a model-bound copy) is required for signature verification.
    /// </summary>
    private async Task<(IActionResult? Error, JsonElement Data)> ReadAndVerifyAsync(string? signingSecret, string kind)
    {
        string json;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            json = await reader.ReadToEndAsync();

        if (_webhookOptions.AllowUnverifiedWebhooksInsecure || !_webhookOptions.VerifySignature)
        {
            _logger.LogWarning("Processing unverified Stripe {Kind} webhook (signature verification disabled).", kind);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(signingSecret))
            {
                _logger.LogError(
                    "Stripe {Kind} webhook signing secret is not configured; rejecting request (fail closed). " +
                    "Set STRIPE_WEBHOOK_SECRET (and STRIPE_SUBSCRIPTION_WEBHOOK_SECRET if the endpoints differ).", kind);
                return (StatusCode(500, "Webhook signing secret not configured"), default);
            }

            var signatureHeader = Request.Headers["Stripe-Signature"].FirstOrDefault();
            if (string.IsNullOrEmpty(signatureHeader))
            {
                _logger.LogWarning("Stripe {Kind} webhook missing Stripe-Signature header", kind);
                return (BadRequest("Missing Stripe-Signature header"), default);
            }

            try
            {
                // Throws StripeException if the signature or timestamp does not verify.
                Stripe.EventUtility.ConstructEvent(
                    json, signatureHeader, signingSecret,
                    _webhookOptions.SignatureToleranceSeconds,
                    throwOnApiVersionMismatch: false);
            }
            catch (Stripe.StripeException ex)
            {
                _logger.LogWarning(ex, "Stripe {Kind} webhook signature verification failed", kind);
                return (BadRequest("Invalid Stripe signature"), default);
            }
        }

        try
        {
            return (null, JsonSerializer.Deserialize<JsonElement>(json));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Stripe {Kind} webhook body was not valid JSON", kind);
            return (BadRequest("Invalid JSON payload"), default);
        }
    }

    [HttpPost("subscription")]
    public async Task<IActionResult> HandleSubscriptionWebhook()
    {
        try
        {
            var (verifyError, webhookData) = await ReadAndVerifyAsync(_subscriptionWebhookSecret, "subscription");
            if (verifyError != null)
                return verifyError;

            // Extract event type to filter relevant events
            if (!webhookData.TryGetProperty("type", out var eventTypeElement))
            {
                _logger.LogWarning("Received subscription webhook without event type");
                return BadRequest("Missing event type");
            }

            var eventType = eventTypeElement.GetString();

            _logger.LogInformation("Received Stripe subscription webhook: {WebhookType}", eventType);

            // Subscriptions are activated when the Checkout Session completes. Stripe also
            // delivers customer.subscription.* and invoice.* events to this endpoint, but
            // those carry subscription/invoice IDs rather than the checkout session ID we
            // use as the job ID, so we acknowledge and ignore them here.
            if (eventType != "checkout.session.completed")
            {
                _logger.LogInformation("Ignoring subscription webhook event type: {EventType}", eventType);
                return Ok(new { status = "ignored", event_type = eventType });
            }

            // For checkout.session.completed, data.object.id is the Checkout Session ID,
            // which was used as the job ID when the subscription checkout was created.
            string? sessionId = null;
            if (webhookData.TryGetProperty("data", out var dataElement) &&
                dataElement.TryGetProperty("object", out var objectElement) &&
                objectElement.TryGetProperty("id", out var idElement))
            {
                sessionId = idElement.GetString();
            }

            if (string.IsNullOrEmpty(sessionId))
            {
                _logger.LogWarning("Received subscription webhook without checkout session ID");
                return BadRequest("Missing checkout session ID");
            }

            _logger.LogInformation("Received subscription webhook for checkout session {SessionId}", sessionId);

            // Find the subscription job in the subscription channel
            var subscriptionJobContext = CreateSubscriptionJobContext();
            Job? job = await FindSubscriptionJob(sessionId, subscriptionJobContext);
            
            if (job == null)
            {
                _logger.LogWarning("Subscription job {SessionId} not found", sessionId);
                return NotFound($"Job {sessionId} not found");
            }

            // If job is already completed, skip processing (idempotency)
            if (job.Status == JobStatus.Completed)
            {
                _logger.LogInformation("Subscription webhook for {SessionId} already processed, skipping", sessionId);
                return Ok(new { status = "already_processed", job_id = sessionId, message = "Webhook already processed" });
            }

            var subscriptionPlan = job.Parameters.TryGetValue("subscription_plan", out var planObj)
                ? planObj?.ToString() ?? "basic"
                : "basic";

            // Resolve key using the same logic as QuotaChecker
            var key = ResolveKeyFromJob(job);
            await UpdateUserQuotaForSubscription(key, subscriptionPlan);

            // Update the job in the subscription channel it actually lives in (the channel is
            // part of the Redis key, so the default payment-channel context would not find it).
            await subscriptionJobContext.UpdateJobAsync(job, JobStatus.Completed, 
                output: JsonSerializer.Serialize(new { status = "subscription_activated", plan = subscriptionPlan }));

            _logger.LogInformation("Successfully processed subscription webhook for job {SessionId}", sessionId);

            return Ok(new { status = "completed", job_id = sessionId, subscription_plan = subscriptionPlan });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing subscription webhook");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost("payment")]
    public async Task<IActionResult> HandlePaymentWebhook()
    {
        try
        {
            var (verifyError, webhookData) = await ReadAndVerifyAsync(_paymentWebhookSecret, "payment");
            if (verifyError != null)
                return verifyError;

            _logger.LogInformation("Received Stripe webhook: {WebhookType}", 
                webhookData.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "unknown");

            // Extract event type to filter relevant events
            if (!webhookData.TryGetProperty("type", out var eventTypeElement))
            {
                _logger.LogWarning("Received webhook without event type");
                return BadRequest("Missing event type");
            }

            var eventType = eventTypeElement.GetString();

            // Only process payment intent events
            if (eventType != "payment_intent.succeeded" && eventType != "payment_intent.payment_failed")
            {
                _logger.LogInformation("Ignoring webhook event type: {EventType}", eventType);
                return Ok(new { status = "ignored", event_type = eventType });
            }
            // Extract payment intent ID from webhook data
            string? paymentIntentId = null;
            if (webhookData.TryGetProperty("data", out var dataElement) &&
                dataElement.TryGetProperty("object", out var objectElement) &&
                objectElement.TryGetProperty("id", out var idElement))
            {
                paymentIntentId = idElement.GetString();
            }

            if (string.IsNullOrEmpty(paymentIntentId))
            {
                _logger.LogWarning("Received payment webhook without payment intent ID");
                return BadRequest("Missing payment intent ID");
            }

            _logger.LogInformation("Received payment webhook for payment intent {PaymentIntentId}", paymentIntentId);

            // Try to atomically update job from Queued to Completed status
            // Only the first webhook to succeed will actually process the payment
            var wasUpdated = await _jobContext.UpdateJobStatusAsync(paymentIntentId, JobStatus.Queued, JobStatus.Completed, "*", 
                output: JsonSerializer.Serialize(new { status = "payment_processed" }));

            if (!wasUpdated)
            {
                _logger.LogInformation("Payment webhook for {PaymentIntentId} already processed by another request, skipping", paymentIntentId);
                return Ok(new { status = "already_processed", job_id = paymentIntentId, message = "Webhook already processed" });
            }

            // Only the winning webhook gets here - now we can safely process the payment
            // First, get the job to extract payment details
            Job job;
            try
            {
                job = await _jobContext.LookupJobAsync<Job>(paymentIntentId, JobStatus.Completed, "*");
            }
            catch (KeyNotFoundException)
            {
                _logger.LogError("Job {PaymentIntenptId} not found after successful status update - this should not happen", paymentIntentId);
                return StatusCode(500, "Internal server error");
            }

            var quotaIncrease = job.Parameters.TryGetValue("quota_increase", out var quotaObj) && 
                                int.TryParse(quotaObj?.ToString(), out var quota) ? quota : 0;

            var quotaName = job.Parameters.TryGetValue("quota_name", out var quotaNameObj)
                ? quotaNameObj?.ToString() ?? "credits"
                : "credits";

            // Resolve key using the same logic as QuotaChecker
            var key = ResolveKeyFromJob(job);            
            await UpdateUserQuotaForPayment(key, quotaName, quotaIncrease);

            _logger.LogInformation("Successfully processed payment webhook for payment intent {PaymentIntentId}", paymentIntentId);

            return Ok(new { status = "completed", job_id = paymentIntentId, quota_name = quotaName, quota_increase = quotaIncrease });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing payment webhook");
            return StatusCode(500, "Internal server error");
        }
    }

    private async Task UpdateUserQuotaForSubscription(string userId, string subscriptionPlan)
    {
        // Use the plan name as the quota name
        var existingQuota = _dataContext.Quotas
            .FirstOrDefault(q => q.Key == userId && q.QuotaName == subscriptionPlan);

        if (existingQuota != null)
        {
            existingQuota.Type = "postpaid";
            await _dataContext.UpsertAsync(existingQuota, q => q.Key == userId && q.QuotaName == subscriptionPlan);
        }
        else
        {
            var newQuota = _dataContext.CreateQuota();
            newQuota.Key = userId;
            newQuota.QuotaName = subscriptionPlan;
            newQuota.Used = 0;
            newQuota.Limit = 0; // Postpaid starts at 0, allows overages
            newQuota.Type = "postpaid";
            await _dataContext.SaveAsync(newQuota);
        }

        _logger.LogInformation("Updated quota for user {UserId} to plan {Plan} (postpaid)", userId, subscriptionPlan);
    }

    private async Task UpdateUserQuotaForPayment(string userId, string quotaName, int quotaIncrease)
    {
        var existingQuota = _dataContext.Quotas
            .FirstOrDefault(q => q.Key == userId && q.QuotaName == quotaName);

        if (existingQuota != null)
        {
            existingQuota.Limit += quotaIncrease;
            existingQuota.Type = "prepaid";
            await _dataContext.UpsertAsync(existingQuota, q => q.Key == userId && q.QuotaName == quotaName);
        }
        else
        {
            var newQuota = _dataContext.CreateQuota();
            newQuota.Key = userId;
            newQuota.QuotaName = quotaName;
            newQuota.Used = 0;
            newQuota.Limit = quotaIncrease;
            newQuota.Type = "prepaid";
            await _dataContext.SaveAsync(newQuota);
        }

        _logger.LogInformation("Increased {QuotaName} quota for user {UserId} by {Increase}", quotaName, userId, quotaIncrease);
    }

    /// <summary>
    /// Resolves the key from job headers using the same logic as QuotaChecker.
    /// This ensures we update the same quota that was originally checked.
    /// </summary>
    private string ResolveKeyFromJob(Job job)
    {
        // Use the same key resolution logic as QuotaChecker
        var key = HttpContextExtensions.ResolveKey(job.Headers ?? new Dictionary<string, string>(), _options.KeyHeaders);
        
        // Fallback to user ID if no headers match
        return string.IsNullOrEmpty(key) ? job.User?.Id ?? "" : key;
    }

    /// <summary>
    /// Creates a job context bound to the subscription channel. Subscription jobs are always
    /// stored in the stripe_subscription_async channel, which is distinct from the controller's
    /// default (payment) channel and is encoded into the Redis keys.
    /// </summary>
    private RedisJobContext CreateSubscriptionJobContext()
    {
        var subscriptionJobContext = new RedisJobContext();
        subscriptionJobContext.Initialize(_options.ConnectionString, "stripe_subscription_async");
        return subscriptionJobContext;
    }

    /// <summary>
    /// Finds a subscription job in the subscription channel.
    /// Subscription jobs are always in the stripe_subscription_async channel.
    /// </summary>
    private async Task<Job?> FindSubscriptionJob(string sessionId, RedisJobContext subscriptionJobContext)
    {
        try
        {
            return await subscriptionJobContext.LookupJobAsync<Job>(sessionId, JobStatus.Queued, "*");
        }
        catch (KeyNotFoundException) { }

        try
        {
            return await subscriptionJobContext.LookupJobAsync<Job>(sessionId, JobStatus.Completed, "*");
        }
        catch (KeyNotFoundException) { }

        return null;
    }

    [HttpGet("success")]
    public async Task<IActionResult> Success([FromQuery] string session_id)
    {
        try
        {
            _logger.LogInformation("User reached success page for session {SessionId}", session_id);

            // Look up the subscription job (session_id is the job ID for subscriptions)
            Job? job = await FindSubscriptionJob(session_id, CreateSubscriptionJobContext());
            
            if (job != null)
            {
                var subscriptionPlan = job.Parameters.TryGetValue("subscription_plan", out var planObj)
                    ? planObj?.ToString() ?? "basic"
                    : "basic";

                return Ok(new { 
                    message = "Subscription successful!", 
                    session_id = session_id,
                    subscription_plan = subscriptionPlan,
                    status = job.Status.ToString()
                });
            }

            // Fallback response if job not found
            return Ok(new { 
                message = "Subscription checkout completed!", 
                session_id = session_id,
                note = "Your subscription will be activated once payment is confirmed."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing success page for session {SessionId}", session_id);
            return Ok(new { 
                message = "Subscription checkout completed!", 
                session_id = session_id,
                note = "Your subscription will be activated once payment is confirmed."
            });
        }
    }

    [HttpGet("cancel")]
    public IActionResult Cancel([FromQuery] string session_id)
    {
        _logger.LogInformation("User canceled checkout for session {SessionId}", session_id);
        
        return Ok(new { 
            message = "Subscription canceled", 
            session_id = session_id,
            note = "No charges were made. You can try again anytime."
        });
    }

}

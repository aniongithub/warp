using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

using Microsoft.Extensions.Options;
using Warp.Core.Data;
using Warp.Core.Job;
using Warp.Core.Job.Contexts;
using Warp.Latinum.Attributes;

namespace Warp.Latinum.Controllers;

[ApiController]
[Route("/stripe")]
[StripeController]
public class StripeWebhookController : ControllerBase
{
    private readonly IDataContext _dataContext;
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly StripeWebhookOptions _options;
    private readonly IJobContext _jobContext;

    public StripeWebhookController(IDataContext dataContext, ILogger<StripeWebhookController> logger, IOptions<StripeWebhookOptions> options)
    {
        _dataContext = dataContext;
        _logger = logger;
        _options = options.Value;
        
        // Initialize JobContext once in constructor
        var jobContext = new RedisJobContext();
        jobContext.Initialize(_options.ConnectionString, _options.Channel);
        _jobContext = jobContext;
    }

    [HttpPost("subscription/{jobId}")]
    public async Task<IActionResult> HandleSubscriptionWebhook(string jobId, [FromBody] object webhookData)
    {
        try
        {
            _logger.LogInformation("Received subscription webhook for job {JobId}", jobId);

            Job? job = null;
            foreach (JobStatus status in Enum.GetValues(typeof(JobStatus)))
            {
                job = await _jobContext.LookupJobAsync<Job>(jobId, status, "");
                if (job != null) break;
            }
            
            if (job == null)
            {
                _logger.LogWarning("Job {JobId} not found", jobId);
                return NotFound($"Job {jobId} not found");
            }

            var subscriptionPlan = job.Parameters.TryGetValue("subscription_plan", out var planObj)
                ? planObj?.ToString() ?? "basic"
                : "basic";

            await UpdateUserQuotaForSubscription(job.User?.Id ?? "", subscriptionPlan);

            await _jobContext.UpdateJobAsync(job, JobStatus.Completed, 
                output: JsonSerializer.Serialize(new { status = "subscription_activated", plan = subscriptionPlan }));

            _logger.LogInformation("Successfully processed subscription webhook for job {JobId}", jobId);

            return Ok(new { status = "completed", job_id = jobId, subscription_plan = subscriptionPlan });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing subscription webhook for job {JobId}", jobId);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost("payment")]
    public async Task<IActionResult> HandlePaymentWebhook([FromBody] JsonElement webhookData)
    {
        try
        {
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

            // Look up job using payment intent ID as job ID
            Job? job = null;
            foreach (JobStatus status in Enum.GetValues(typeof(JobStatus)))
            {
                job = await _jobContext.LookupJobAsync<Job>(paymentIntentId, status, "*");
                if (job != null) break;
            }
            
            if (job == null)
            {
                _logger.LogWarning("Job not found for payment intent {PaymentIntentId}", paymentIntentId);
                return NotFound($"Job not found for payment intent {paymentIntentId}");
            }

            var quotaIncrease = job.Parameters.TryGetValue("quota_increase", out var quotaObj) && 
                                int.TryParse(quotaObj?.ToString(), out var quota) ? quota : 0;

            var quotaName = job.Parameters.TryGetValue("quota_name", out var quotaNameObj)
                ? quotaNameObj?.ToString() ?? "credits"
                : "credits";

            await UpdateUserQuotaForPayment(job.User?.Id ?? "", quotaName, quotaIncrease);

            await _jobContext.UpdateJobAsync(job, JobStatus.Completed, 
                output: JsonSerializer.Serialize(new { status = "payment_processed", quota_name = quotaName, quota_added = quotaIncrease }));

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


}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Warp.Core.Data;
using Warp.Core.Job;
using Warp.Core.Job.Contexts;

namespace Warp.Latinum.Controllers;

[ApiController]
[Route("webhook/stripe")]
public class StripeWebhookController : ControllerBase
{
    private readonly IDataContext _dataContext;
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly IConfiguration _configuration;

    public StripeWebhookController(IDataContext dataContext, ILogger<StripeWebhookController> logger, IConfiguration configuration)
    {
        _dataContext = dataContext;
        _logger = logger;
        _configuration = configuration;
    }

    private RedisJobContext CreateJobContext(string channel)
    {
        var redisConfig = _configuration.GetSection("Redis");
        var connectionString = redisConfig.GetValue<string>("ConnectionString") ?? "localhost:6379";
        
        var jobContext = new RedisJobContext();
        jobContext.Initialize(connectionString, channel);
        
        return jobContext;
    }

    [HttpPost("subscription/{jobId}")]
    public async Task<IActionResult> HandleSubscriptionWebhook(string jobId, [FromBody] object webhookData)
    {
        try
        {
            _logger.LogInformation("Received subscription webhook for job {JobId}", jobId);

            var jobContext = CreateJobContext("stripe_subscription_async");

            Job? job = null;
            foreach (JobStatus status in Enum.GetValues(typeof(JobStatus)))
            {
                job = await jobContext.LookupJobAsync<Job>(jobId, status, "");
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

            await jobContext.UpdateJobAsync(job, JobStatus.Completed, 
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

    [HttpPost("payment/{jobId}")]
    public async Task<IActionResult> HandlePaymentWebhook(string jobId, [FromBody] object webhookData)
    {
        try
        {
            _logger.LogInformation("Received payment webhook for job {JobId}", jobId);

            var jobContext = CreateJobContext("stripe_payment_async");

            Job? job = null;
            foreach (JobStatus status in Enum.GetValues(typeof(JobStatus)))
            {
                job = await jobContext.LookupJobAsync<Job>(jobId, status, "");
                if (job != null) break;
            }
            
            if (job == null)
            {
                _logger.LogWarning("Job {JobId} not found", jobId);
                return NotFound($"Job {jobId} not found");
            }

            var quotaIncrease = job.Parameters.TryGetValue("quota_increase", out var quotaObj) && 
                                int.TryParse(quotaObj?.ToString(), out var quota) ? quota : 0;

            var quotaName = job.Parameters.TryGetValue("quota_name", out var quotaNameObj)
                ? quotaNameObj?.ToString() ?? "credits"
                : "credits";

            await UpdateUserQuotaForPayment(job.User?.Id ?? "", quotaName, quotaIncrease);

            await jobContext.UpdateJobAsync(job, JobStatus.Completed, 
                output: JsonSerializer.Serialize(new { status = "payment_processed", quota_name = quotaName, quota_added = quotaIncrease }));

            _logger.LogInformation("Successfully processed payment webhook for job {JobId}", jobId);

            return Ok(new { status = "completed", job_id = jobId, quota_name = quotaName, quota_increase = quotaIncrease });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing payment webhook for job {JobId}", jobId);
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

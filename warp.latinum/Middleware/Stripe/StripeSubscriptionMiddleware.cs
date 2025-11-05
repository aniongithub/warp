using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Warp.Core.Data;
using Warp.Core.Job;
using Warp.Core.Job.Contexts;
using Warp.Core.Middleware;
using Warp.Dilithium.Middleware;
using Stripe;
using Stripe.Checkout;

namespace Warp.Latinum.Middleware.Stripe;



public class StripeSubscriptionPlan
{
    // Configuration (from YAML)
    public string PlanId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string ProductDescription { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "usd";
    public string Interval { get; set; } = "month";
    public int IntervalCount { get; set; } = 1;
    public string? QuotaName { get; set; }
    public string? QuotaType { get; set; } = "postpaid";
    
    // Runtime (populated by attribute)
    public string StripeProductId { get; set; } = string.Empty; // prod_xxx
    public string StripePriceId { get; set; } = string.Empty;   // price_xxx
}

public class StripeSubscriptionOptions : AsyncApiHandlerOptions
{
    public StripeSubscriptionPlan[] Plans { get; set; } = Array.Empty<StripeSubscriptionPlan>();

    // Stripe configuration
    public string? WebhookUrl { get; set; }
    public string StripeSecretKey { get; set; } = string.Empty;
    public string StripePublishableKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public int SessionExpirationMinutes { get; set; } = 30;
    public string StripeApiBase { get; set; } = "https://api.stripe.com";
    public string SuccessUrl { get; set; } = "http://0.0.0.0:5004/stripe/success";
    public string CancelUrl { get; set; } = "http://0.0.0.0:5004/stripe/cancel";
    // ConnectionString and Channel are inherited from AsyncApiHandlerOptions
    
    // Webhook display name for Stripe dashboard
    public string WebhookName { get; set; } = "Warp Monetization Webhook";
}

public class StripeSubscriptionMiddleware : AsyncApiHandler<StripeSubscriptionOptions, RedisJobContext>
{
    private readonly SessionService _sessionService;

    public StripeSubscriptionMiddleware(
        string name,
        ILogger logger,
        IDataContext dataContext, 
        StripeSubscriptionOptions options) 
        : base(name, logger, dataContext, options)
    {
        if (options.Plans.Length == 0)
            throw new ArgumentException("Plans array must be specified in StripeSubscriptionOptions");

        // Validate plans
        foreach (var plan in options.Plans)
        {
            if (string.IsNullOrEmpty(plan.PlanId))
                throw new ArgumentException("All plans must have a PlanId");
            if (string.IsNullOrEmpty(plan.ProductName))
                throw new ArgumentException($"Plan '{plan.PlanId}' must have a ProductName");
        }

        // Configure Stripe client
        var stripeClient = new StripeClient(
            apiKey: options.StripeSecretKey,
            apiBase: options.StripeApiBase
        );
        
        _sessionService = new SessionService(stripeClient);
    }

    protected override async Task<Job> CreateJobAsync(IUser user, Dictionary<string, object?> extractedInputs, Dictionary<string, ParameterMapping> parameterMappings, JobRoutingInfo routingInfo, TracingContext tracingContext)
    {
        // Extract planId from the original path: /subscription/create/{planId}
        var segments = routingInfo.OriginalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var planId = segments.Length >= 3 && segments[^2] == "create" && segments[^3] == "subscription" 
            ? segments[^1] 
            : null;

        if (string.IsNullOrEmpty(planId))
            throw new ArgumentException("planId is required for subscription creation");

        // Find the plan
        var plan = Options.Plans.FirstOrDefault(p => p.PlanId == planId);
        if (plan == null)
            throw new ArgumentException($"Plan '{planId}' not found");

        // Create Stripe subscription checkout session
        var session = await CreateStripeSubscriptionSession(user.Id!, user.Email!, plan, extractedInputs);

        // Enhance parameters with subscription-specific data
        var enhancedParameters = new Dictionary<string, object?>(extractedInputs)
        {
            ["type"] = "stripe_subscription",
            ["subscription_plan"] = plan.PlanId,
            ["session_id"] = session.Id,
            ["checkout_url"] = session.Url,
            ["quota_type"] = plan.QuotaType ?? "postpaid",
            ["quota_name"] = plan.QuotaName
        };

        // Create the job using session ID as the job ID
        var job = new Job
        {
            Id = session.Id, // Use checkout session ID as job ID
            User = user,
            QueuedAt = DateTime.UtcNow,
            Status = JobStatus.Queued,
            OriginalPath = routingInfo.OriginalPath,
            ClusterId = routingInfo.ClusterId,
            TargetDestination = routingInfo.TargetDestination,
            Parameters = enhancedParameters,
            Headers = routingInfo.Headers,
            ParameterMappings = parameterMappings,
            TraceParent = tracingContext.TraceParent,
            TraceState = tracingContext.TraceState
        };

        return job;
    }

    protected override async Task<object> GetSubmitResponse(Job job)
    {
        // Return subscription-specific response with checkout URL
        return new {
            session_id = job.Parameters["session_id"],
            checkout_url = job.Parameters["checkout_url"],
            job_id = job.Id,
            subscription_plan = job.Parameters["subscription_plan"]
        };
    }

    private async Task<Session> CreateStripeSubscriptionSession(string userId, string userEmail, StripeSubscriptionPlan plan, Dictionary<string, object?> extractedInputs)
    {
        try
        {
            Logger.LogInformation("Creating Stripe subscription session for user {UserId} with plan {PlanId} (price {PriceId})", 
                userId, plan.PlanId, plan.StripePriceId);

            var options = new SessionCreateOptions
            {
                PaymentMethodTypes = new List<string> { "card" },
                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions
                    {
                        Price = plan.StripePriceId,
                        Quantity = 1,
                    },
                },
                Mode = "subscription",
                SuccessUrl = Options.SuccessUrl,
                CancelUrl = Options.CancelUrl,
                CustomerEmail = userEmail,
                ClientReferenceId = userId, // Store user ID for webhook processing
                ExpiresAt = DateTime.UtcNow.AddMinutes(Options.SessionExpirationMinutes),
                Metadata = new Dictionary<string, string>
                {
                    ["user_id"] = userId,
                    ["plan_id"] = plan.PlanId,
                    ["quota_name"] = plan.QuotaName ?? ""
                }
            };

            var session = await _sessionService.CreateAsync(options);
            return session;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create Stripe subscription session for user {UserId} with plan {PlanId}", userId, plan.PlanId);
            throw;
        }
    }
}
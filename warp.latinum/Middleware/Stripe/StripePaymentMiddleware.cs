using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Warp.Core.Data;
using Warp.Core.Job;
using Warp.Core.Job.Contexts;
using Warp.Dilithium.Middleware;

namespace Warp.Latinum.Middleware.Stripe;

public class StripePaymentOptions : AsyncApiHandlerOptions
{
    public decimal CurrencyMultiplier { get; set; } = 1000; // Default: $1 = 1000 quota units
    public string? WebhookUrl { get; set; }
    public string StripeSecretKey { get; set; } = string.Empty;
    public string StripePublishableKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string Currency { get; set; } = "usd";
    public int PaymentIntentExpirationMinutes { get; set; } = 30;
    // ConnectionString and Channel are inherited from AsyncApiHandlerOptions
}

public class StripePaymentMiddleware : AsyncApiHandler<StripePaymentOptions, RedisJobContext>
{
    public StripePaymentMiddleware(
        string name,
        ILogger<StripePaymentMiddleware> logger,
        IDataContext dataContext, 
        StripePaymentOptions options) 
        : base(name, logger, dataContext, options)
    {
    }

    protected override async Task<string> CreateAndEnqueueJobAsync(IUser user, Dictionary<string, object?> extractedInputs, Dictionary<string, ParameterMapping> parameterMappings, JobRoutingInfo routingInfo, TracingContext tracingContext)
    {
        // Extract and validate payment amount
        if (!extractedInputs.TryGetValue("amount", out var amountObj) || 
            !decimal.TryParse(amountObj?.ToString(), out var amount))
            throw new ArgumentException("Valid amount is required");

        if (amount <= 0)
            throw new ArgumentException("Amount must be greater than zero");

        // Create Stripe payment intent
        var paymentIntentId = await CreateStripePaymentIntent(user.Id!, amount, extractedInputs);

        // Calculate quota increase based on currency multiplier
        var quotaIncrease = (int)(amount * Options.CurrencyMultiplier);

        // Enhance parameters with payment-specific data
        var enhancedParameters = new Dictionary<string, object?>(extractedInputs)
        {
            ["type"] = "stripe_payment",
            ["amount"] = amount,
            ["quota_increase"] = quotaIncrease,
            ["payment_intent_id"] = paymentIntentId,
            ["webhook_url"] = Options.WebhookUrl ?? $"/webhook/stripe/payment/{Guid.NewGuid()}",
            ["quota_type"] = "prepaid",
            ["client_secret"] = GetClientSecret(paymentIntentId)
        };

        // Create the job with enhanced parameters
        var job = new Job
        {
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

        await JobContext.EnqueueJobAsync(job);
        return job.Id;
    }

    private async Task<string> CreateStripePaymentIntent(string userId, decimal amount, Dictionary<string, object?> extractedInputs)
    {
        // TODO: Implement Stripe API call to create payment intent
        // This would use the Stripe .NET SDK to create a payment intent
        // Amount should be in cents for Stripe
        var amountInCents = (long)(amount * 100);
        
        Logger.LogInformation("Creating Stripe payment intent for user {UserId} with amount {Amount} {Currency}", 
            userId, amount, Options.Currency);
        
        // For now, return a mock payment intent ID
        await Task.Delay(100); // Simulate API call
        return $"pi_test_{Guid.NewGuid():N}";
    }

    private string GetClientSecret(string paymentIntentId)
    {
        return $"{paymentIntentId}_secret_{Guid.NewGuid():N}";
    }
}
using System;
using System.Collections.Generic;
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

namespace Warp.Latinum.Middleware.Stripe;

public class StripePaymentOptions : AsyncApiHandlerOptions
{
    public decimal CurrencyMultiplier { get; set; } = 1000; // Default: $1 = 1000 quota units
    public string StripeSecretKey { get; set; } = string.Empty;
    public string StripePublishableKey { get; set; } = string.Empty;
    public string Currency { get; set; } = "usd";
    public int PaymentIntentExpirationMinutes { get; set; } = 30;
    public string StripeApiBase { get; set; } = "https://api.stripe.com"; // Default to real Stripe, can override for LocalStripe
    // ConnectionString and Channel are inherited from AsyncApiHandlerOptions
}

public class StripePaymentMiddleware : AsyncApiHandler<StripePaymentOptions, RedisJobContext>
{
    private readonly PaymentIntentService _paymentIntentService;

    public StripePaymentMiddleware(
        string name,
        ILogger logger,
        IDataContext dataContext, 
        StripePaymentOptions options) 
        : base(name, logger, dataContext, options)
    {
        // Configure Stripe client with custom base URL for LocalStripe
        var stripeClient = new StripeClient(
            apiKey: options.StripeSecretKey,
            apiBase: options.StripeApiBase
        );
        
        _paymentIntentService = new PaymentIntentService(stripeClient);
    }

    protected override async Task<Job> CreateJobAsync(IUser user, Dictionary<string, object?> extractedInputs, Dictionary<string, ParameterMapping> parameterMappings, JobRoutingInfo routingInfo, TracingContext tracingContext)
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

        // Get client secret from payment intent
        var clientSecret = await GetClientSecretAsync(paymentIntentId);

        // Enhance parameters with payment-specific data
        var parameters = new Dictionary<string, object?>(extractedInputs)
        {
            ["type"] = "stripe_payment",
            ["amount"] = amount,
            ["quota_increase"] = quotaIncrease,
            ["payment_intent_id"] = paymentIntentId,
            ["quota_type"] = "prepaid",
            ["quota_name"] = "credits",
            ["client_secret"] = clientSecret
        };

        // Create the job using payment intent ID as the job ID
        var job = new Job
        {
            Id = paymentIntentId, // Use payment intent ID as job ID
            User = user,
            QueuedAt = DateTime.UtcNow,
            Status = JobStatus.Queued,
            OriginalPath = routingInfo.OriginalPath,
            ClusterId = routingInfo.ClusterId,
            TargetDestination = routingInfo.TargetDestination,
            Parameters = parameters,
            Headers = routingInfo.Headers,
            ParameterMappings = parameterMappings,
            TraceParent = tracingContext.TraceParent,
            TraceState = tracingContext.TraceState
        };

        return job;
    }

    protected override async Task<object> GetSubmitResponse(Job job)
    {
        // Return payment-specific response with client_secret
        return new {
            client_secret = job.Parameters["client_secret"],
            payment_intent_id = job.Parameters["payment_intent_id"],
            job_id = job.Id,
            amount = job.Parameters["amount"],
            quota_increase = job.Parameters["quota_increase"]
        };
    }

    private async Task<string> CreateStripePaymentIntent(string userId, decimal amount, Dictionary<string, object?> extractedInputs)
    {
        try
        {
            // Amount should be in cents for Stripe
            var amountInCents = (long)(amount * 100);
            
            Logger.LogInformation("Creating Stripe payment intent for user {UserId} with amount {Amount} {Currency}", 
                userId, amount, Options.Currency);

            var options = new PaymentIntentCreateOptions
            {
                Amount = amountInCents,
                Currency = Options.Currency,
                PaymentMethodTypes = new List<string> { "card" },
                Metadata = new Dictionary<string, string>
                {
                    // ["user_id"] = userId,
                    ["quota_increase"] = ((int)(amount * Options.CurrencyMultiplier)).ToString()
                }
            };
            
            var paymentIntent = await _paymentIntentService.CreateAsync(options);
            return paymentIntent.Id;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create Stripe payment intent for user {UserId}", userId);
            throw;
        }
    }

    private async Task<string> GetClientSecretAsync(string paymentIntentId)
    {
        try
        {
            var paymentIntent = await _paymentIntentService.GetAsync(paymentIntentId);
            return paymentIntent.ClientSecret ?? throw new Exception("Payment intent has no client secret");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to retrieve client secret for payment intent {PaymentIntentId}", paymentIntentId);
            throw;
        }
    }
}
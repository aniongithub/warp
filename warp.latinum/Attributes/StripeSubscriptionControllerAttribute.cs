using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Stripe;
using Warp.Latinum.Controllers;
using Warp.Latinum.Middleware.Stripe;

namespace Warp.Latinum.Attributes;

/// <summary>
/// Attribute for Stripe subscription controllers.
/// At startup: Creates/updates Stripe Products and Prices, populates plan configuration.
/// In DEBUG: Automatically starts ngrok and registers subscription webhooks with Stripe API.
/// In RELEASE: Uses configured webhook URL for deployment.
/// </summary>
public class StripeSubscriptionControllerAttribute : PaymentControllerAttribute
{
    /// <summary>
    /// Array of Stripe subscription events to listen for (optional)
    /// </summary>
    public string[]? Events { get; set; }

    public override async Task ConfigureAsync(IServiceProvider serviceProvider, IConfigurationSection optionsSection)
    {
        var httpClient = serviceProvider.GetRequiredService<HttpClient>();
        var logger = serviceProvider.GetRequiredService<ILogger<StripeSubscriptionControllerAttribute>>();

        // Bind configuration to options
        var options = new StripeSubscriptionOptions();
        optionsSection.Bind(options);

        // Initialize Stripe client
        var stripeClient = new StripeClient(options.StripeSecretKey);

        // Step 1: Create/update Stripe Products and Prices
        await ConfigureStripeProductsAndPricesAsync(stripeClient, logger, options);

        // Step 2: Configure webhooks
#if DEBUG
        await ConfigureDebugWebhookAsync(httpClient, logger, options);
#else
        await ConfigureProductionWebhookAsync(httpClient, logger, options);
#endif
    }

    private async Task ConfigureStripeProductsAndPricesAsync(StripeClient stripeClient, ILogger logger, StripeSubscriptionOptions options)
    {
        logger.LogInformation("Configuring Stripe Products and Prices for {PlanCount} subscription plans", options.Plans.Length);

        var productService = new ProductService(stripeClient);
        var priceService = new PriceService(stripeClient);

        foreach (var plan in options.Plans)
        {
            try
            {
                // Create or update Stripe Product
                var product = await CreateOrUpdateStripeProductAsync(productService, logger, plan);
                plan.StripeProductId = product.Id;

                // Create or update Stripe Price
                var price = await CreateOrUpdateStripePriceAsync(priceService, logger, plan, product.Id);
                plan.StripePriceId = price.Id;

                logger.LogInformation("Configured plan '{PlanId}': Product {ProductId}, Price {PriceId}", 
                    plan.PlanId, plan.StripeProductId, plan.StripePriceId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to configure Stripe resources for plan '{PlanId}'", plan.PlanId);
                throw;
            }
        }

        logger.LogInformation("Successfully configured all Stripe Products and Prices");
    }

    private async Task<Product> CreateOrUpdateStripeProductAsync(ProductService productService, ILogger logger, StripeSubscriptionPlan plan)
    {
        // Try to find existing product by metadata
        var listOptions = new ProductListOptions
        {
            Limit = 100,
            Active = true
        };

        var products = await productService.ListAsync(listOptions);
        var existingProduct = products.Data.FirstOrDefault(p => 
            p.Metadata.ContainsKey("plan_id") && p.Metadata["plan_id"] == plan.PlanId);

        if (existingProduct != null)
        {
            logger.LogDebug("Found existing Stripe Product {ProductId} for plan '{PlanId}'", existingProduct.Id, plan.PlanId);
            
            // Update product if needed
            var updateOptions = new ProductUpdateOptions
            {
                Name = plan.ProductName,
                Description = plan.ProductDescription,
                Metadata = new Dictionary<string, string>
                {
                    ["plan_id"] = plan.PlanId,
                    ["quota_name"] = plan.QuotaName ?? "",
                    ["quota_type"] = plan.QuotaType ?? "postpaid"
                }
            };

            return await productService.UpdateAsync(existingProduct.Id, updateOptions);
        }
        else
        {
            logger.LogInformation("Creating new Stripe Product for plan '{PlanId}'", plan.PlanId);
            
            var createOptions = new ProductCreateOptions
            {
                Name = plan.ProductName,
                Description = plan.ProductDescription,
                Type = "service", // For subscription services
                Metadata = new Dictionary<string, string>
                {
                    ["plan_id"] = plan.PlanId,
                    ["quota_name"] = plan.QuotaName ?? "",
                    ["quota_type"] = plan.QuotaType ?? "postpaid"
                }
            };

            return await productService.CreateAsync(createOptions);
        }
    }

    private async Task<Price> CreateOrUpdateStripePriceAsync(PriceService priceService, ILogger logger, 
        StripeSubscriptionPlan plan, string productId)
    {
        // Try to find existing active price for this product with matching amount and interval
        var listOptions = new PriceListOptions
        {
            Product = productId,
            Active = true,
            Limit = 100
        };

        var prices = await priceService.ListAsync(listOptions);
        var existingPrice = prices.Data.FirstOrDefault(p => 
            p.UnitAmount == (long)(plan.Amount * 100) && // Stripe uses cents
            p.Recurring?.Interval == plan.Interval &&
            p.Recurring?.IntervalCount == plan.IntervalCount);

        if (existingPrice != null)
        {
            logger.LogDebug("Found existing Stripe Price {PriceId} for plan '{PlanId}'", existingPrice.Id, plan.PlanId);
            return existingPrice;
        }
        else
        {
            logger.LogInformation("Creating new Stripe Price for plan '{PlanId}': {Amount} {Currency}/{Interval}", 
                plan.PlanId, plan.Amount, plan.Currency, plan.Interval);
            
            var createOptions = new PriceCreateOptions
            {
                Product = productId,
                UnitAmount = (long)(plan.Amount * 100), // Convert to cents
                Currency = plan.Currency,
                Recurring = new PriceRecurringOptions
                {
                    Interval = plan.Interval,
                    IntervalCount = plan.IntervalCount
                },
                Metadata = new Dictionary<string, string>
                {
                    ["plan_id"] = plan.PlanId,
                    ["quota_name"] = plan.QuotaName ?? "",
                    ["quota_type"] = plan.QuotaType ?? "postpaid"
                }
            };

            return await priceService.CreateAsync(createOptions);
        }
    }

    private async Task ConfigureDebugWebhookAsync(HttpClient httpClient, ILogger logger, StripeSubscriptionOptions options)
    {
        logger.LogInformation("DEBUG mode: Starting ngrok and registering subscription webhook with Stripe API");

        // 1. Start ngrok subprocess
        var ngrokUrl = await StartNgrokAsync(httpClient, logger, options);
        
        // 2. Register webhook with real Stripe API
        await RegisterWebhookWithStripeAsync(httpClient, logger, options, ngrokUrl);
    }

    private async Task ConfigureProductionWebhookAsync(HttpClient httpClient, ILogger logger, StripeSubscriptionOptions options)
    {
        logger.LogInformation("PRODUCTION mode: Using configured webhook URL for subscriptions");
        
        if (string.IsNullOrEmpty(options.WebhookUrl))
        {
            throw new InvalidOperationException("WebhookUrl is required for production subscription webhook configuration");
        }

        await RegisterWebhookWithStripeAsync(httpClient, logger, options, options.WebhookUrl);
    }

    private async Task<string> StartNgrokAsync(HttpClient httpClient, ILogger logger, StripeSubscriptionOptions options)
    {
        logger.LogInformation("Starting ngrok tunnel for port 5004...");

        // Start ngrok process
        var startInfo = new ProcessStartInfo
        {
            FileName = "ngrok",
            Arguments = $"http 5004 --log=stdout --log-level=info",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start ngrok process");
        }

        // Wait for ngrok to start and get the URL from ngrok API
        await Task.Delay(3000); // Give ngrok time to start

        // Query ngrok local API for tunnel URL
        var ngrokApiUrl = await GetNgrokTunnelUrlAsync(httpClient, logger);
        
        logger.LogInformation("Ngrok tunnel established: {NgrokUrl}", ngrokApiUrl);
        return ngrokApiUrl;
    }

    private async Task<string> GetNgrokTunnelUrlAsync(HttpClient httpClient, ILogger logger)
    {
        try
        {
            var response = await httpClient.GetAsync("http://127.0.0.1:4040/api/tunnels");
            var jsonResponse = await response.Content.ReadAsStringAsync();
            var tunnelsData = JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            foreach (var tunnel in tunnelsData.GetProperty("tunnels").EnumerateArray())
            {
                if (tunnel.GetProperty("proto").GetString() == "https")
                {
                    return tunnel.GetProperty("public_url").GetString()!;
                }
            }

            throw new InvalidOperationException("No HTTPS tunnel found in ngrok API response");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get ngrok tunnel URL from API");
            throw;
        }
    }

    private async Task RegisterWebhookWithStripeAsync(HttpClient httpClient, ILogger logger, 
        StripeSubscriptionOptions options, string webhookUrl)
    {
        logger.LogInformation("Registering subscription webhook with Stripe API: {WebhookUrl}", webhookUrl);

        var fullWebhookUrl = $"{webhookUrl}/stripe/subscription";
        var events = Events ?? new[] { 
            "checkout.session.completed", 
            "customer.subscription.created",
            "customer.subscription.updated",
            "customer.subscription.deleted",
            "invoice.payment_succeeded",
            "invoice.payment_failed"
        };
        var webhookName = options.WebhookName; // Use configurable webhook name

        // Use Stripe secret key for authentication
        var authBytes = Encoding.ASCII.GetBytes($"{options.StripeSecretKey}:");
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        // Step 1: Check if webhook already exists
        var existingWebhookId = await FindExistingWebhookAsync(httpClient, logger, webhookName);

        if (!string.IsNullOrEmpty(existingWebhookId))
        {
            // Step 2a: Update existing webhook
            logger.LogInformation("Updating existing Stripe subscription webhook {WebhookId} with URL: {WebhookUrl}", existingWebhookId, fullWebhookUrl);
            await UpdateWebhookAsync(httpClient, logger, existingWebhookId, fullWebhookUrl, events);
        }
        else
        {
            // Step 2b: Create new webhook
            logger.LogInformation("Creating new Stripe subscription webhook with URL: {WebhookUrl}", fullWebhookUrl);
            await CreateWebhookAsync(httpClient, logger, fullWebhookUrl, events, webhookName);
        }
    }

    private async Task<string?> FindExistingWebhookAsync(HttpClient httpClient, ILogger logger, string webhookName)
    {
        try
        {
            var response = await httpClient.GetAsync("https://api.stripe.com/v1/webhook_endpoints?limit=100");
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to list existing webhooks: {StatusCode}", response.StatusCode);
                return null;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var webhooksData = JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            foreach (var webhook in webhooksData.GetProperty("data").EnumerateArray())
            {
                if (webhook.TryGetProperty("metadata", out var metadata) &&
                    metadata.TryGetProperty("name", out var nameElement) &&
                    nameElement.GetString() == webhookName)
                {
                    return webhook.GetProperty("id").GetString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error checking for existing webhooks");
            return null;
        }
    }

    private async Task UpdateWebhookAsync(HttpClient httpClient, ILogger logger, string webhookId, string webhookUrl, string[] events)
    {
        var formData = new List<KeyValuePair<string, string>>
        {
            new("url", webhookUrl)
        };

        foreach (var evt in events)
        {
            formData.Add(new KeyValuePair<string, string>("enabled_events[]", evt));
        }

        var content = new FormUrlEncodedContent(formData);
        var response = await httpClient.PostAsync($"https://api.stripe.com/v1/webhook_endpoints/{webhookId}", content);

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Successfully updated Stripe subscription webhook {WebhookId}: {WebhookUrl}", webhookId, webhookUrl);
        }
        else
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            logger.LogWarning("Failed to update Stripe subscription webhook {WebhookId}: {StatusCode} - {Error}", webhookId, response.StatusCode, errorContent);
            
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                throw new InvalidOperationException($"Failed to update Stripe subscription webhook: {response.StatusCode} - {errorContent}");
            }
        }
    }

    private async Task CreateWebhookAsync(HttpClient httpClient, ILogger logger, string webhookUrl, string[] events, string webhookName)
    {
        var formData = new List<KeyValuePair<string, string>>
        {
            new("url", webhookUrl),
            new("description", webhookName), // Display name in Stripe dashboard
            new("metadata[name]", webhookName) // Add metadata for identification
        };

        foreach (var evt in events)
        {
            formData.Add(new KeyValuePair<string, string>("enabled_events[]", evt));
        }

        var content = new FormUrlEncodedContent(formData);
        var response = await httpClient.PostAsync("https://api.stripe.com/v1/webhook_endpoints", content);

        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync();
            logger.LogInformation("Successfully created Stripe subscription webhook: {WebhookUrl}", webhookUrl);
            logger.LogDebug("Stripe API response: {Response}", responseContent);
        }
        else
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            logger.LogWarning("Failed to create Stripe subscription webhook: {StatusCode} - {Error}", response.StatusCode, errorContent);
            
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                throw new InvalidOperationException($"Failed to create Stripe subscription webhook: {response.StatusCode} - {errorContent}");
            }
        }
    }
}
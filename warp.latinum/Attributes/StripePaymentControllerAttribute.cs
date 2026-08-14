using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Warp.Latinum.Middleware.Stripe;

namespace Warp.Latinum.Attributes;

/// <summary>
/// Attribute for Stripe payment controllers.
/// In DEBUG: Automatically starts ngrok and registers webhooks with Stripe API.
/// In RELEASE: Uses configured webhook URL for deployment.
/// </summary>
public class StripePaymentControllerAttribute : PaymentControllerAttribute
{
    /// <summary>
    /// Array of Stripe events to listen for (optional)
    /// </summary>
    public string[]? Events { get; set; }

    public override async Task ConfigureAsync(IServiceProvider serviceProvider, IConfigurationSection optionsSection)
    {
        var httpClient = serviceProvider.GetRequiredService<HttpClient>();
        var logger = serviceProvider.GetRequiredService<ILogger<StripePaymentControllerAttribute>>();

        // Bind configuration to options
        var options = new StripePaymentOptions();
        optionsSection.Bind(options);

#if DEBUG
        await ConfigureDebugWebhookAsync(httpClient, logger, options);
#else
        await ConfigureProductionWebhookAsync(httpClient, logger, options);
#endif
    }

    private async Task ConfigureDebugWebhookAsync(HttpClient httpClient, ILogger logger, StripePaymentOptions options)
    {
        logger.LogInformation("DEBUG mode: Starting ngrok and registering webhook with Stripe API");

        // 1. Start ngrok subprocess
        var ngrokUrl = await StartNgrokAsync(httpClient, logger, options);
        
        // 2. Register webhook with real Stripe API
        await RegisterWebhookWithStripeAsync(httpClient, logger, options, ngrokUrl);
    }

    private async Task ConfigureProductionWebhookAsync(HttpClient httpClient, ILogger logger, StripePaymentOptions options)
    {
        logger.LogInformation("PRODUCTION mode: Using configured webhook URL");
        
        if (string.IsNullOrEmpty(options.CallbackUrl))
        {
            throw new InvalidOperationException("CallbackUrl is required for production webhook configuration");
        }

        await RegisterWebhookWithStripeAsync(httpClient, logger, options, options.CallbackUrl);
    }

    private async Task<string> StartNgrokAsync(HttpClient httpClient, ILogger logger, StripePaymentOptions options)
    {
        // Reuse an existing ngrok tunnel if one is already running. Free ngrok
        // accounts allow only one simultaneous agent session, and the payment and
        // subscription webhooks share a single tunnel (same base URL, different
        // paths), so starting a second ngrok agent would fail.
        var existingTunnel = await TryGetNgrokTunnelUrlAsync(httpClient);
        if (!string.IsNullOrEmpty(existingTunnel))
        {
            logger.LogInformation("Reusing existing ngrok tunnel: {NgrokUrl}", existingTunnel);
            return existingTunnel;
        }

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

        // Add auth token if available
        if (!string.IsNullOrEmpty(options.NgrokAuthToken))
        {
            startInfo.FileName = "bash";
            startInfo.Arguments = $"-c \"ngrok config add-authtoken {options.NgrokAuthToken} && ngrok http 5004 --log=stdout --log-level=info\"";
        }

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

    private async Task<string?> TryGetNgrokTunnelUrlAsync(HttpClient httpClient)
    {
        try
        {
            var response = await httpClient.GetAsync("http://127.0.0.1:4040/api/tunnels");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var tunnelsData = JsonSerializer.Deserialize<JsonElement>(jsonResponse);

            if (!tunnelsData.TryGetProperty("tunnels", out var tunnels))
            {
                return null;
            }

            foreach (var tunnel in tunnels.EnumerateArray())
            {
                if (tunnel.GetProperty("proto").GetString() == "https")
                {
                    return tunnel.GetProperty("public_url").GetString();
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task RegisterWebhookWithStripeAsync(HttpClient httpClient, ILogger logger, StripePaymentOptions options, string webhookUrl)
    {
        logger.LogInformation("Registering webhook with Stripe API: {WebhookUrl}", webhookUrl);

        var fullWebhookUrl = $"{webhookUrl}/stripe/payment";
        var events = Events ?? options.Events ?? new[] { "payment_intent.succeeded", "payment_intent.payment_failed" };
        var webhookName = options.WebhookName; // Use configurable webhook name

        // Use Stripe secret key for authentication
        var authBytes = Encoding.ASCII.GetBytes($"{options.StripeSecretKey}:");
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        // Resolve the Stripe API base (trim trailing slash to avoid building "//v1/...").
        var apiBase = options.StripeApiBase.TrimEnd('/');

        // Step 1: Check if webhook already exists
        var existingWebhookId = await FindExistingWebhookAsync(httpClient, logger, apiBase, webhookName);

        if (!string.IsNullOrEmpty(existingWebhookId))
        {
            // Step 2a: Update existing webhook
            logger.LogInformation("Updating existing Stripe webhook {WebhookId} with URL: {WebhookUrl}", existingWebhookId, fullWebhookUrl);
            await UpdateWebhookAsync(httpClient, logger, apiBase, existingWebhookId, fullWebhookUrl, events);
        }
        else
        {
            // Step 2b: Create new webhook
            logger.LogInformation("Creating new Stripe webhook with URL: {WebhookUrl}", fullWebhookUrl);
            await CreateWebhookAsync(httpClient, logger, apiBase, fullWebhookUrl, events, webhookName);
        }
    }

    private async Task<string?> FindExistingWebhookAsync(HttpClient httpClient, ILogger logger, string apiBase, string webhookName)
    {
        try
        {
            var response = await httpClient.GetAsync($"{apiBase}/v1/webhook_endpoints?limit=100");
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

    private async Task UpdateWebhookAsync(HttpClient httpClient, ILogger logger, string apiBase, string webhookId, string webhookUrl, string[] events)
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
        var response = await httpClient.PostAsync($"{apiBase}/v1/webhook_endpoints/{webhookId}", content);

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Successfully updated Stripe webhook {WebhookId}: {WebhookUrl}", webhookId, webhookUrl);
        }
        else
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            logger.LogWarning("Failed to update Stripe webhook {WebhookId}: {StatusCode} - {Error}", webhookId, response.StatusCode, errorContent);
            
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                throw new InvalidOperationException($"Failed to update Stripe webhook: {response.StatusCode} - {errorContent}");
            }
        }
    }

    private async Task CreateWebhookAsync(HttpClient httpClient, ILogger logger, string apiBase, string webhookUrl, string[] events, string webhookName)
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
        var response = await httpClient.PostAsync($"{apiBase}/v1/webhook_endpoints", content);

        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync();
            logger.LogInformation("Successfully created Stripe webhook: {WebhookUrl}", webhookUrl);
            logger.LogDebug("Stripe API response: {Response}", responseContent);
        }
        else
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            logger.LogWarning("Failed to create Stripe webhook: {StatusCode} - {Error}", response.StatusCode, errorContent);
            
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                throw new InvalidOperationException($"Failed to create Stripe webhook: {response.StatusCode} - {errorContent}");
            }
        }
    }
}
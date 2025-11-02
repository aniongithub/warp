using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Warp.Latinum.Controllers;

namespace Warp.Latinum.Attributes;

/// <summary>
/// Attribute for Stripe payment controllers.
/// In DEBUG: Automatically starts ngrok and registers webhooks with Stripe API.
/// In RELEASE: Uses configured webhook URL for deployment.
/// </summary>
public class StripeControllerAttribute : PaymentControllerAttribute
{
    /// <summary>
    /// Name of the webhook to register
    /// </summary>
    public string? WebhookName { get; set; }

    /// <summary>
    /// Array of Stripe events to listen for (optional)
    /// </summary>
    public string[]? Events { get; set; }

    public override async Task ConfigureAsync(IServiceProvider serviceProvider, IConfigurationSection optionsSection)
    {
        var httpClient = serviceProvider.GetRequiredService<HttpClient>();
        var logger = serviceProvider.GetRequiredService<ILogger<StripeControllerAttribute>>();

        // Bind configuration to options
        var options = new StripeWebhookOptions();
        optionsSection.Bind(options);

#if DEBUG
        await ConfigureDebugWebhookAsync(httpClient, logger, options);
#else
        await ConfigureProductionWebhookAsync(httpClient, logger, options);
#endif
    }

    private async Task ConfigureDebugWebhookAsync(HttpClient httpClient, ILogger logger, StripeWebhookOptions options)
    {
        logger.LogInformation("DEBUG mode: Starting ngrok and registering webhook with Stripe API");

        // 1. Start ngrok subprocess
        var ngrokUrl = await StartNgrokAsync(httpClient, logger, options);
        
        // 2. Register webhook with real Stripe API
        await RegisterWebhookWithStripeAsync(httpClient, logger, options, ngrokUrl);
    }

    private async Task ConfigureProductionWebhookAsync(HttpClient httpClient, ILogger logger, StripeWebhookOptions options)
    {
        logger.LogInformation("PRODUCTION mode: Using configured webhook URL");
        
        if (string.IsNullOrEmpty(options.CallbackUrl))
        {
            throw new InvalidOperationException("CallbackUrl is required for production webhook configuration");
        }

        await RegisterWebhookWithStripeAsync(httpClient, logger, options, options.CallbackUrl);
    }

    private async Task<string> StartNgrokAsync(HttpClient httpClient, ILogger logger, StripeWebhookOptions options)
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

        // Add auth token if available
        if (!string.IsNullOrEmpty(options.NgrokAuthToken))
        {
            startInfo.Arguments = $"config add-authtoken {options.NgrokAuthToken} && {startInfo.Arguments}";
            startInfo.FileName = "bash";
            startInfo.Arguments = $"-c \"ngrok {startInfo.Arguments}\"";
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

    private async Task RegisterWebhookWithStripeAsync(HttpClient httpClient, ILogger logger, StripeWebhookOptions options, string webhookUrl)
    {
        logger.LogInformation("Registering webhook with Stripe API: {WebhookUrl}", webhookUrl);

        var fullWebhookUrl = $"{webhookUrl}/stripe/payment";
        var events = Events ?? options.Events ?? new[] { "payment_intent.succeeded", "payment_intent.payment_failed" };

        // Create form data for Stripe API
        var formData = new List<KeyValuePair<string, string>>
        {
            new("url", fullWebhookUrl)
        };

        foreach (var evt in events)
        {
            formData.Add(new KeyValuePair<string, string>("enabled_events[]", evt));
        }

        var content = new FormUrlEncodedContent(formData);

        // Use Stripe secret key for authentication
        var authBytes = Encoding.ASCII.GetBytes($"{options.StripeSecretKey}:");
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        var response = await httpClient.PostAsync("https://api.stripe.com/v1/webhook_endpoints", content);

        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync();
            logger.LogInformation("Successfully registered Stripe webhook: {WebhookUrl}", fullWebhookUrl);
            logger.LogDebug("Stripe API response: {Response}", responseContent);
        }
        else
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            logger.LogWarning("Failed to register Stripe webhook: {StatusCode} - {Error}", response.StatusCode, errorContent);
            
            // Don't throw in DEBUG mode - the app should still work even if webhook registration fails
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                throw new InvalidOperationException($"Failed to register Stripe webhook: {response.StatusCode} - {errorContent}");
            }
        }
    }
}
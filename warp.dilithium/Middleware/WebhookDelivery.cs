using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Warp.Core.Data;
using Warp.Core.Job;
using Warp.Core.Middleware;

namespace Warp.Dilithium.Middleware;

public class WebhookDeliveryOptions : MiddlewareOptions
{
    public string WebhookUrl { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 5;
}

/// <summary>
/// Middleware that delivers job results via webhook when used in warp.plasma Postdispatch pipeline
/// </summary>
public class WebhookDelivery : MiddlewareBase<WebhookDeliveryOptions>
{
    private readonly HttpClient _httpClient;

    public WebhookDelivery(string name, ILogger logger, IDataContext dataContext, WebhookDeliveryOptions options, HttpClient httpClient)
        : base(name, logger, dataContext, options)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    }

    protected override async Task<IResult> ProcessAsync(HttpContext context)
    {
        // Only deliver webhooks if we have a job in the context (warp.plasma usage)
        if (context.Items.TryGetValue("Job", out var jobObj) && jobObj is IJob job)
        {
            // Only deliver if job is completed or failed
            if (job.Status == JobStatus.Completed || job.Status == JobStatus.Failed)
            {
                await DeliverWebhookAsync(job, context, context.RequestAborted);
            }
        }
        
        // Always continue the pipeline
        return Results.Empty.Continue();
    }

    private async Task DeliverWebhookAsync(IJob job, HttpContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(Options.WebhookUrl))
        {
            Logger.LogWarning("Webhook URL not configured for job {JobId}, skipping delivery", job.Id);
            return;
        }

        // Replace {jobId} placeholder in webhook URL
        var webhookUrl = Options.WebhookUrl.Replace("{jobId}", job.Id);

        var payload = await CreateWebhookPayloadAsync(job, context);
        string jsonPayload = JsonSerializer.Serialize(payload);

        int attempt = 0;
        while (attempt <= Options.MaxRetries)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                // Add custom headers
                foreach (var header in Options.Headers)
                {
                    request.Headers.Add(header.Key, header.Value);
                }

                Logger.LogDebug("Sending webhook for job {JobId} to {WebhookUrl} (attempt {Attempt})",
                    job.Id, webhookUrl, attempt + 1);

                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    Logger.LogInformation("Successfully delivered job {JobId} result via webhook to {WebhookUrl}",
                        job.Id, webhookUrl);
                    return;
                }
                else
                {
                    var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    Logger.LogWarning("Webhook delivery failed for job {JobId}: HTTP {StatusCode} {ReasonPhrase}. Response: {Response}",
                        job.Id, (int)response.StatusCode, response.ReasonPhrase, responseContent);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Exception during webhook delivery for job {JobId} (attempt {Attempt})",
                    job.Id, attempt + 1);
            }

            attempt++;
            if (attempt <= Options.MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Options.RetryDelaySeconds * attempt);
                Logger.LogDebug("Retrying webhook delivery for job {JobId} in {DelaySeconds} seconds",
                    job.Id, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        Logger.LogError("Failed to deliver job {JobId} result via webhook after {MaxRetries} attempts",
            job.Id, Options.MaxRetries + 1);
    }

    private async Task<object> CreateWebhookPayloadAsync(IJob job, HttpContext context)
    {
        // Read the actual response from the middleware pipeline
        string? responseBody = null;
        if (context.Response.Body != null && context.Response.Body.Length > 0)
        {
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
            responseBody = await reader.ReadToEndAsync();
            context.Response.Body.Seek(0, SeekOrigin.Begin); // Reset for any subsequent middleware
        }

        // Include response headers that were set by middleware (like quota usage)
        var responseHeaders = new Dictionary<string, string>();
        foreach (var header in context.Response.Headers)
        {
            responseHeaders[header.Key] = string.Join(", ", header.Value.ToArray());
        }

        return new
        {
            jobId = job.Id,
            status = job.Status.ToString(),
            userId = job.User?.Id,
            userEmail = job.User?.Email,
            queuedAt = job.QueuedAt,
            startedAt = job.StartedAt,
            endedAt = job.EndedAt,
            error = job.Error,
            result = responseBody ?? job.Output, // Use pipeline response if available, fallback to job output
            originalPath = job.OriginalPath,
            parameters = job.Parameters,
            httpStatusCode = context.Response.StatusCode,
            responseHeaders = responseHeaders,
            timestamp = DateTime.UtcNow
        };
    }
}
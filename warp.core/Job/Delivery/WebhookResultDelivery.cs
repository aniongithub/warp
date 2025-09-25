using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Warp.Core.Job.Delivery;

public class WebhookDeliveryOptions
{
    public string WebhookUrl { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 5;
}

public class WebhookResultDelivery : ResultDeliveryBase<WebhookDeliveryOptions>
{
    private readonly HttpClient _httpClient;

    public WebhookResultDelivery(string name, ILogger logger, HttpClient httpClient, WebhookDeliveryOptions options)
        : base(name, logger, options)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    }

    public override async Task DeliverAsync(IJob job, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(Options.WebhookUrl))
        {
            Logger.LogWarning("Webhook URL not configured for job {JobId}, skipping delivery", job.Id);
            return;
        }

        string jsonPayload = JsonSerializer.Serialize(new
        {
            jobId = job.Id,
            result = job.Output,
            timestamp = DateTime.UtcNow
        });

        int attempt = 0;
        while (attempt <= Options.MaxRetries)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Options.WebhookUrl);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                // Add custom headers
                foreach (var header in Options.Headers)
                {
                    request.Headers.Add(header.Key, header.Value);
                }

                Logger.LogDebug("Sending webhook for job {JobId} to {WebhookUrl} (attempt {Attempt})",
                    job.Id, Options.WebhookUrl, attempt + 1);

                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    Logger.LogInformation("Successfully delivered job {JobId} result via webhook to {WebhookUrl}",
                        job.Id, Options.WebhookUrl);
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

    private object CreateWebhookPayload(IJob job)
    {
        return new
        {
            JobId = job.Id,
            Status = job.Status.ToString(),
            UserId = job.User?.Id,
            UserEmail = job.User?.Email,
            QueuedAt = job.QueuedAt,
            StartedAt = job.StartedAt,
            EndedAt = job.EndedAt,
            Error = job.Error,
            Output = job.Output,
            OriginalPath = job.OriginalPath,
            Parameters = job.Parameters
        };
    }
}

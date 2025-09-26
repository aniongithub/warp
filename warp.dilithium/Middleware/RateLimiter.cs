using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Warp.Dilithium.Middleware;

using Warp.Core.Data;
using Warp.Core.Middleware;

public class RateLimiterOptions : MiddlewareOptions
{
    public List<string> KeyHeaders { get; set; } = new() { "x-api-key", "X-JWT-Email" };
    public List<string> RateHeaders { get; set; } = new() { "x-api-key-ratelimit" }; // Additional headers to check for rate limiting
    public float RateLimitHz { get; set; } = 5;
}

public sealed class RateLimiter : MiddlewareBase<RateLimiterOptions>
{
    public RateLimiter(string name, ILogger logger, IDataContext context, RateLimiterOptions options)
        : base(name, logger, context, options)
    {
    }

    protected async override Task<IResult> ProcessAsync(HttpContext context)
    {
        string? key = null;
        // Check KeyHeaders first
        foreach (var header in Options.KeyHeaders)
        {
            key = context.Request.Headers[header].FirstOrDefault()
                ?? context.Request.Query[header].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(key))
                break;
        }
        key ??= "anonymous";

        float? dynamicRateLimit = null;
        foreach (var header in Options.RateHeaders)
        {
            string? dynamicRateLimitStr = context.Request.Headers[header].FirstOrDefault()
                ?? context.Request.Query[header].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(dynamicRateLimitStr))
            {
                if (float.TryParse(dynamicRateLimitStr, out var rateLimit))
                {
                    dynamicRateLimit = rateLimit;
                    break; // Use the first valid rate limit found
                }
                else
                    Logger.LogWarning("Invalid rate limit value in header {Header}: {Value}", header, dynamicRateLimitStr);
            }
        }
        float rateLimitHz = dynamicRateLimit ?? Options.RateLimitHz;
        float maxTokens = rateLimitHz; // Allow burst up to rate limit

        var now = DateTime.UtcNow;
        var request = DataContext.Requests.OrderByDescending(r => r.LastUsed).FirstOrDefault(r => r.Key == key);
        float tokens = maxTokens;
        DateTime lastUsed = now;
        if (request != null)
        {
            lastUsed = request.LastUsed;
            // Calculate how many tokens to refill since last request
            var elapsed = (now - lastUsed).TotalSeconds;
            tokens = Math.Min(maxTokens, request.LastRate + (float)(elapsed * rateLimitHz));
        }
        if (tokens < 1)
        {
            return Results
                .Problem("Rate limit exceeded", statusCode: 429)
                .Stop(); // Stop the pipeline on rate limit exceeded
        }
        else
        {
            if (request != null)
            {
                request.LastUsed = now;
                request.LastRate = tokens - 1; // Consume one token
                await DataContext.SaveAsync(request);
            }
            else
            {
                var newRequest = DataContext.CreateRequest();
                newRequest.Key = key;
                newRequest.LastUsed = now;
                newRequest.LastRate = maxTokens - 1;
                await DataContext.SaveAsync(newRequest);
            }
        }
        return Results
            .Ok()
            .Continue(); // This middleware allows the request to continue
    }
}

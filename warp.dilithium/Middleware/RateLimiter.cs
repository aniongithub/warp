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

        // Atomically evaluate and update the token bucket. This closes the read-modify-write race in
        // the previous implementation, where concurrent requests read the same LastRate and each
        // wrote back their own decrement, losing updates and letting callers exceed the rate limit.
        var allowed = await DataContext.TryConsumeRateLimitAsync(key, rateLimitHz, maxTokens, DateTime.UtcNow);
        if (!allowed)
        {
            return Results
                .Problem("Rate limit exceeded", statusCode: 429)
                .Stop(); // Stop the pipeline on rate limit exceeded
        }

        return Results
            .Ok()
            .Continue(); // This middleware allows the request to continue
    }
}

namespace Warp.Middleware;

using Warp.Core.Data;

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

    protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
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
        if (dynamicRateLimit != null)
            Options.RateLimitHz = dynamicRateLimit.Value;

        var now = DateTime.UtcNow;
        var request = DataContext.Requests.OrderByDescending(r => r.LastUsed).FirstOrDefault(r => r.Key == key);
        float lastRate = request?.LastRate ?? 0;
        DateTime lastUsed = request?.LastUsed ?? DateTime.MinValue;
        var elapsed = (now - lastUsed).TotalSeconds;
        // Exponential decay
        var rate = lastRate * Math.Exp(-elapsed * Options.RateLimitHz);
        if (rate + 1 > Options.RateLimitHz)
        {
            context.Response.StatusCode = 429;
            await context.Response.WriteAsync("Rate limit exceeded.");
            // Do not return; let pipeline continue to postprocess middleware
        }
        else
        {
            if (request != null)
            {
                request.LastUsed = now;
                request.LastRate = (float)(rate + 1);
                await DataContext.SaveAsync(request);
            }
            else
            {
                var newRequest = DataContext.CreateRequest();
                newRequest.Key = key;
                newRequest.LastUsed = now;
                newRequest.LastRate = 1;

                await DataContext.SaveAsync(newRequest);
            }
        }
        await next(context);
    }
}

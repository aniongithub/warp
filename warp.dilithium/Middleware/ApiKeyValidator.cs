using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Warp.Core.Data;
using Warp.Core.Middleware;

namespace Warp.Dilithium.Middleware;

public class ApiKeyValidatorOptions : MiddlewareOptions
{
    public string HeaderName { get; set; } = "x-api-key";
}

public sealed class ApiKeyValidator : MiddlewareBase<ApiKeyValidatorOptions>
{
    public ApiKeyValidator(string name, ILogger logger, IDataContext context, ApiKeyValidatorOptions options)
        : base(name, logger, context, options)
    {
    }

    protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var _headerName = Options.HeaderName;
        if (!context.Request.Headers.TryGetValue(_headerName, out var apiKeyHeader))
        {
            Logger.LogWarning("Missing API key in header: {HeaderName}", _headerName);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
            }
        }
        else
        {
            var apiKey = apiKeyHeader.ToString().Trim();
            Logger.LogDebug("Extracted API key: {ApiKey}", apiKey);
            var validKey = DataContext.ApiKeys.FirstOrDefault(k => k.Key == apiKey && k.IsActive);
            if (validKey == null)
            {
                Logger.LogWarning("Invalid or inactive API key: {ApiKey}", apiKey);
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Invalid API key.");
            }
            else
            {
                // Add x-api-key-ratelimit header for downstream use
                context.Request.Headers["x-api-key-ratelimit"] = validKey.RateLimitHz.ToString();
                // Add user identifier header for downstream use
                context.Request.Headers["X-JWT-Email"] = validKey.Owner;
            }
        }
        await next(context);
    }
}

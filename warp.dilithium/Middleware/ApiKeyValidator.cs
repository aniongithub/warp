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

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
    protected override async Task<IResult> ProcessAsync(HttpContext context)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
    {
        var _headerName = Options.HeaderName;
        if (!context.Request.Headers.TryGetValue(_headerName, out var apiKeyHeader))
        {
            Logger.LogWarning("Missing API key in header: {HeaderName}", _headerName);
            return Results
                .Problem(statusCode: 401, title: "Unauthorized", detail: "Missing API key.")
                .Stop();
        }
        else
        {
            var apiKey = apiKeyHeader.ToString().Trim();
            Logger.LogDebug("Extracted API key: {ApiKey}", apiKey);
            var validKey = DataContext.ApiKeys.FirstOrDefault(k => k.Key == apiKey && k.IsActive);
            if (validKey == null)
            {
                return Results
                    .Problem(statusCode: 401, title: "Unauthorized", detail: $"Invalid or inactive API key: {apiKey}")
                    .Stop();
            }
            else
            {
                // Add x-api-key-ratelimit header for downstream use
                context.Request.Headers["x-api-key-ratelimit"] = validKey.RateLimitHz.ToString();
                // Add user identifier header for downstream use
                context.Request.Headers["X-JWT-Email"] = validKey.Owner;
            }
        }
        
        return Results
            .Ok()
            .Continue();
    }
}

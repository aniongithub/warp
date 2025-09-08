using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Warp.Core.Data;
using Warp.Core.Middleware;

namespace Warp.Dilithium.Middleware
{
    public class QuotaCheckerOptions : MiddlewareOptions
    {
        public string? QuotaName { get; set; }
        public string? QuotaNameHeader { get; set; }
        public List<string> KeyHeaders { get; set; } = new() { "X-JWT-Email", "X-Api-Key" };
        public bool CreateQuotaIfNotFound { get; set; } = true;
        public float QuotaLimit { get; set; } = 10.0f;
        public string? QuotaLimitHeader { get; set; }
        public string QuotaType { get; set; } = "prepaid";
    }

    public sealed class QuotaChecker : MiddlewareBase<QuotaCheckerOptions>
    {
        public QuotaChecker(string name, ILogger logger, IDataContext context, QuotaCheckerOptions options)
            : base(name, logger, context, options) { }

        protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            // 1. Resolve quota name from options or header
            string quotaName = Options.QuotaName ?? string.Empty;
            if (string.IsNullOrEmpty(quotaName) && !string.IsNullOrEmpty(Options.QuotaNameHeader) && context.Request.Headers.TryGetValue(Options.QuotaNameHeader, out var headerVals))
                quotaName = headerVals.FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrEmpty(quotaName))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Missing quota name.");
                return;
            }

            // 2. Get user or API key identifier (from header or context)
            string key = ResolveKey(context);
            if (string.IsNullOrEmpty(key))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync($"Missing key header: {Options.KeyHeaders}");
                return;
            }

            // 3. Lookup quota usage and limit
            var quota = DataContext.Quotas.FirstOrDefault(q => q.Key == key && q.QuotaName == quotaName);

            if (quota == null)
            {
                if (Options.CreateQuotaIfNotFound)
                {
                    float limit = Options.QuotaLimit;
                    if (!string.IsNullOrEmpty(Options.QuotaLimitHeader) && context.Request.Headers.TryGetValue(Options.QuotaLimitHeader, out var limitHeaderVals))
                    {
                        float.TryParse(limitHeaderVals.FirstOrDefault(), out limit);
                    }
                    quota = DataContext.CreateQuota();
                    quota.Key = key;
                    quota.QuotaName = quotaName;
                    quota.Limit = limit;
                    quota.Used = 0;
                    quota.Type = Options.QuotaType;
                    await DataContext.SaveAsync(quota);
                }
                else
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync($"No quota found for key '{key}' and quota '{quotaName}'.");
                    return;
                }
            }

            // 4. Check quota based on type - prepaid blocks when exhausted, postpaid just passes through
            switch (quota.Type)
            {
                case "prepaid":
                    if (quota.Used >= quota.Limit)
                    {
                        context.Response.StatusCode = 429;
                        await context.Response.WriteAsync($"Quota exhausted for '{quotaName}'.");
                        return;
                    }
                    break;
                case "postpaid":
                    // postpaid always allowed, usage tracking happens in QuotaUpdater
                    break;
                default:
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsync($"Unknown quota type '{quota.Type}' for quota '{quotaName}'.");
                    return;
            }

            // Store quota context for QuotaUpdater middleware to use later
            context.Items["QuotaChecker.QuotaId"] = quota.Id;
            context.Items["QuotaChecker.QuotaName"] = quotaName;
            context.Items["QuotaChecker.Key"] = key;

            // Continue to next middleware - QuotaUpdater will handle actual consumption
            await next(context);
        }

        private string ResolveKey(HttpContext context)
        {
            if (Options.KeyHeaders != null)
            {
                foreach (var header in Options.KeyHeaders)
                {
                    if (!string.IsNullOrEmpty(header) && context.Request.Headers.TryGetValue(header, out var headerVals))
                    {
                        var val = headerVals.FirstOrDefault();
                        if (!string.IsNullOrEmpty(val))
                            return val;
                    }
                }
            }
            return string.Empty;
        }
    }
}

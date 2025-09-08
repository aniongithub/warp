using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Warp.Core.Data;
using Warp.Core.Middleware;

namespace Warp.Dilithium.Middleware
{
    public class QuotaUpdaterOptions : MiddlewareOptions
    {
        public string UsageHeader { get; set; } = "X-Quota-Usage";
        public float DefaultUsage { get; set; } = 1.0f;
        public bool OnlyOnSuccess { get; set; } = true;
        public List<int> SuccessStatusCodes { get; set; } = new() { 200, 201, 202, 204 };
    }

    public sealed class QuotaUpdater : MiddlewareBase<QuotaUpdaterOptions>
    {
        public QuotaUpdater(string name, ILogger logger, IDataContext context, QuotaUpdaterOptions options)
            : base(name, logger, context, options) { }

        protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            // Continue to next middleware first
            await next(context);

            // Check if QuotaChecker set context for us
            if (!context.Items.ContainsKey("QuotaChecker.QuotaId"))
            {
                // No quota context - QuotaChecker wasn't run or didn't find a quota
                return;
            }

            // Extract quota context set by QuotaChecker
            var quotaId = context.Items["QuotaChecker.QuotaId"]?.ToString();
            var quotaName = context.Items["QuotaChecker.QuotaName"]?.ToString();
            var key = context.Items["QuotaChecker.Key"]?.ToString();

            if (string.IsNullOrEmpty(quotaId) || string.IsNullOrEmpty(key))
            {
                Logger.LogWarning("QuotaUpdater: Invalid quota context from QuotaChecker");
                return;
            }

            // Check if we should update quota based on response status
            if (Options.OnlyOnSuccess && !Options.SuccessStatusCodes.Contains(context.Response.StatusCode))
            {
                // Request failed, don't consume quota
                return;
            }

            // Determine usage amount
            float usage = Options.DefaultUsage;
            if (context.Response.Headers.TryGetValue(Options.UsageHeader, out var usageHeaderValues))
            {
                var usageHeaderValue = usageHeaderValues.FirstOrDefault();
                if (!string.IsNullOrEmpty(usageHeaderValue) && float.TryParse(usageHeaderValue, out var parsedUsage))
                {
                    usage = parsedUsage;
                }
            }

            // Skip if no usage to record
            if (usage <= 0)
            {
                return;
            }

            // Find and update the quota
            var quota = DataContext.Quotas.FirstOrDefault(q => q.Id == quotaId);
            if (quota != null)
            {
                quota.Used += usage;
                await DataContext.SaveAsync(quota);
                
                Logger.LogDebug("QuotaUpdater: Updated quota '{QuotaName}' for key '{Key}' by {Usage} units", 
                    quotaName, key, usage);
            }
            else
            {
                Logger.LogWarning("QuotaUpdater: Could not find quota with ID '{QuotaId}' for update", quotaId);
            }
        }
    }
}

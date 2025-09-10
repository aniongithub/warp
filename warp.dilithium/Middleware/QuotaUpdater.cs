using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Warp.Core.Data;
using Warp.Core.Middleware;

namespace Warp.Dilithium.Middleware
{
    public class QuotaUpdaterOptions : MiddlewareOptions
    {
        public string UsageHeader { get; set; } = "X-Quota-Usage";
        public string QuotaHeader { get; set; } = "X-Quota-Id";
        public float DefaultUsage { get; set; } = 1.0f;
        public bool OnlyOnSuccess { get; set; } = true;
        public List<int> SuccessStatusCodes { get; set; } = new() { 200, 201, 202, 204 };
    }

    public sealed class QuotaUpdater : MiddlewareBase<QuotaUpdaterOptions>
    {
        public QuotaUpdater(string name, ILogger logger, IDataContext context, QuotaUpdaterOptions options)
            : base(name, logger, context, options) { }

        protected override async Task<IResult> ProcessAsync(HttpContext context)
        {
            // Check if QuotaChecker set quota ID header for us
            if (!context.Request.Headers.TryGetValue(Options.QuotaHeader, out var quotaIdValues))
            {
                // No quota header - QuotaChecker wasn't run or didn't find a quota
                return Results.Ok().Continue();
            }

            var quotaId = quotaIdValues.FirstOrDefault();
            if (string.IsNullOrEmpty(quotaId))
            {
                Logger.LogWarning("QuotaUpdater: Empty quota ID in header '{QuotaHeader}'", Options.QuotaHeader);
                return Results.Ok().Continue();
            }

            // Check if we should update quota based on response status
            if (Options.OnlyOnSuccess && !Options.SuccessStatusCodes.Contains(context.Response.StatusCode))
            {
                // Request failed, don't consume quota
                return Results.Ok().Continue();
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
                return Results.Ok().Continue();
            }

            // Find and update the quota
            var quota = DataContext.Quotas.FirstOrDefault(q => q.Id == quotaId);
            if (quota != null)
            {
                quota.Used += usage;
                await DataContext.SaveAsync(quota);
                
                Logger.LogDebug("QuotaUpdater: Updated quota '{QuotaName}' (ID: {QuotaId}) by {Usage} units", 
                    quota.QuotaName, quotaId, usage);
            }
            else
            {
                Logger.LogWarning("QuotaUpdater: Could not find quota with ID '{QuotaId}' for update", quotaId);
            }

            return Results.Ok().Continue();
        }
    }
}

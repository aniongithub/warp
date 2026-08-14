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
        public string ReservedHeader { get; set; } = "X-Quota-Reserved";
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

            var succeeded = !Options.OnlyOnSuccess || Options.SuccessStatusCodes.Contains(context.Response.StatusCode);

            // Determine the actual usage amount (usually only known post-response, e.g. token counts).
            float usage = Options.DefaultUsage;
            if (context.Response.Headers.TryGetValue(Options.UsageHeader, out var usageHeaderValues))
            {
                var usageHeaderValue = usageHeaderValues.FirstOrDefault();
                if (!string.IsNullOrEmpty(usageHeaderValue) &&
                    float.TryParse(usageHeaderValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedUsage))
                {
                    usage = parsedUsage;
                }
            }

            // Reservation path: QuotaChecker already RESERVED an amount up-front (prepaid admission
            // control). We must NOT consume again here - instead reconcile the reservation against the
            // actual usage so the two together equal the true cost.
            if (context.Request.Headers.TryGetValue(Options.ReservedHeader, out var reservedValues) &&
                float.TryParse(reservedValues.FirstOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var reserved))
            {
                if (!succeeded)
                {
                    // Request failed and we only bill successes: release the whole reservation.
                    await DataContext.SettleQuotaAsync(quotaId, -reserved);
                    Logger.LogDebug("QuotaUpdater: Released reservation of {Reserved} for failed request (quota ID: {QuotaId})",
                        reserved, quotaId);
                }
                else
                {
                    // Settle the delta between what was reserved and what was actually used. Positive
                    // charges the remainder, negative refunds the over-reservation, zero is a no-op.
                    var delta = usage - reserved;
                    await DataContext.SettleQuotaAsync(quotaId, delta);
                    Logger.LogDebug("QuotaUpdater: Settled reservation for quota (ID: {QuotaId}); reserved {Reserved}, actual {Usage}, delta {Delta}",
                        quotaId, reserved, usage, delta);
                }

                return Results.Ok().Continue();
            }

            // No reservation (e.g. postpaid, or QuotaChecker not run): record actual usage post-response.
            if (!succeeded)
            {
                // Request failed, don't consume quota
                return Results.Ok().Continue();
            }

            // Skip if no usage to record
            if (usage <= 0)
            {
                return Results.Ok().Continue();
            }

            // Atomically consume the quota. This closes the read-modify-write race in the previous
            // implementation (concurrent `quota.Used += usage; SaveAsync(quota)` could lose updates
            // and overrun the limit). Enforcement of the prepaid limit happens inside the store.
            var result = await DataContext.TryConsumeQuotaAsync(quotaId, usage);
            switch (result)
            {
                case QuotaConsumeResult.Consumed:
                    Logger.LogDebug("QuotaUpdater: Consumed {Usage} units from quota (ID: {QuotaId})",
                        usage, quotaId);
                    break;
                case QuotaConsumeResult.LimitExceeded:
                    Logger.LogWarning("QuotaUpdater: Quota (ID: {QuotaId}) is exhausted; {Usage} units not recorded",
                        quotaId, usage);
                    break;
                case QuotaConsumeResult.NotFound:
                    Logger.LogWarning("QuotaUpdater: Could not find quota with ID '{QuotaId}' for update", quotaId);
                    break;
            }

            return Results.Ok().Continue();
        }
    }
}

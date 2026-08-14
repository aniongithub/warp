using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Warp.Core.Data;
using Warp.Core.Extensions;
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
        public string QuotaHeader { get; set; } = "X-Quota-Id";

        /// <summary>
        /// Amount of quota reserved up-front at admission time for a prepaid request (before dispatch).
        /// A concurrent burst can no longer be served past the cap because each request atomically
        /// reserves this amount before it is dispatched; when the reservation would exceed the limit the
        /// request is rejected with 429. The actual (usually token-based) usage is only known after the
        /// response, so <see cref="QuotaUpdater"/> reconciles the reservation against it afterwards.
        /// </summary>
        public float ReserveAmount { get; set; } = 1.0f;

        /// <summary>Header used to hand the reserved amount to <see cref="QuotaUpdater"/> for reconciliation.</summary>
        public string ReservedHeader { get; set; } = "X-Quota-Reserved";
    }

    public sealed class QuotaChecker : MiddlewareBase<QuotaCheckerOptions>
    {
        public QuotaChecker(string name, ILogger logger, IDataContext context, QuotaCheckerOptions options)
            : base(name, logger, context, options) { }

        protected override async Task<IResult> ProcessAsync(HttpContext context)
        {
            // 1. Resolve quota name from options or header
            string quotaName = Options.QuotaName ?? string.Empty;
            if (string.IsNullOrEmpty(quotaName) && !string.IsNullOrEmpty(Options.QuotaNameHeader) && context.Request.Headers.TryGetValue(Options.QuotaNameHeader, out var headerVals))
                quotaName = headerVals.FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrEmpty(quotaName))
            {
                return Results
                    .Problem(statusCode: 400, title: "Bad Request", detail: "Missing quota name.")
                    .Stop();
            }

            // 2. Get user or API key identifier (from header or context)
            string key = context.ResolveKey(Options.KeyHeaders);
            if (string.IsNullOrEmpty(key))
            {
                return Results
                    .Problem(statusCode: 401, title: "Unauthorized", detail: $"Missing key header: {string.Join(", ", Options.KeyHeaders)}")
                    .Stop();
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
                    return Results
                        .Problem(statusCode: 403, title: "Forbidden", detail: $"No quota found for key '{key}' and quota '{quotaName}'.")
                        .Stop();
                }
            }

            // 4. Check quota based on type. Prepaid enforces admission control by atomically RESERVING
            //    the per-request amount up-front (before dispatch): if the reservation would exceed the
            //    limit the request is rejected with 429 and never dispatched, so a concurrent burst can
            //    no longer be served past the cap. Postpaid has no cap, so it passes through and usage is
            //    recorded post-response by QuotaUpdater.
            switch (quota.Type)
            {
                case "prepaid":
                    var reserved = Options.ReserveAmount;
                    var reservation = await DataContext.TryConsumeQuotaAsync(quota.Id, reserved);
                    switch (reservation)
                    {
                        case QuotaConsumeResult.Consumed:
                            // Reservation succeeded atomically; hand the reserved amount to QuotaUpdater
                            // so it reconciles against the actual usage instead of double-consuming.
                            context.Request.Headers[Options.ReservedHeader] = reserved.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            break;
                        case QuotaConsumeResult.LimitExceeded:
                            return Results
                                .Problem(statusCode: 429, title: "Too Many Requests", detail: $"Quota exhausted for '{quotaName}'.")
                                .Stop();
                        case QuotaConsumeResult.NotFound:
                            return Results
                                .Problem(statusCode: 500, title: "Internal Server Error", detail: $"Quota '{quotaName}' could not be found for reservation.")
                                .Stop();
                    }
                    break;
                case "postpaid":
                    // postpaid always allowed, usage tracking happens in QuotaUpdater
                    break;
                default:
                    return Results
                        .Problem(statusCode: 500, title: "Internal Server Error", detail: $"Unknown quota type '{quota.Type}' for quota '{quotaName}'.")
                        .Stop();
            }

            // Store quota context for QuotaUpdater middleware to use later
            context.Request.Headers[Options.QuotaHeader] = quota.Id;

            return Results
                .Ok()
                .Continue();
        }


    }
}

using System.Diagnostics;

using Warp.Core.Data;
using Warp.Core.Helper;

namespace Warp.Middleware;

public sealed class OpenTelemetryEnd : MiddlewareBase<MiddlewareOptions>
{
    public OpenTelemetryEnd(string name, ILogger logger, IDataContext context, MiddlewareOptions options)
        : base(name, logger, context, options)
    {
        options.TracingEnabled = true; // Ensure tracing is enabled
    }
    protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        await next(context);
    }
}
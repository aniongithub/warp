using System.Diagnostics;

using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

using Warp.Core.Data;

namespace Warp.Middleware;

public class OpenTelemetryStartOptions : MiddlewareOptions
{
    public List<string> TagHeaders { get; set; } = new();
}

public sealed class OpenTelemetryStart : MiddlewareBase<OpenTelemetryStartOptions>
{
    private static readonly ActivitySource activitySource = new ActivitySource("Warp");
    private string[] _tagHeaders = [];

    public OpenTelemetryStart(string name, ILogger logger, IDataContext context, OpenTelemetryStartOptions options)
        : base(name, logger, context, options)
    {
        options.TracingEnabled = true; // Ensure tracing is enabled

        _tagHeaders = options.TagHeaders
            .Select(h => h.ToLowerInvariant())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToArray();
    }

    protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var operationName = $"{context.Request.Method} {context.GetRequestPath()}";
        var activity = activitySource.StartActivity(operationName, ActivityKind.Server);

        if (activity != null)
        {
            activity.SetTag("http.method", context.Request.Method);
            activity.SetTag("http.target", context.GetRequestPath() + context.Request.QueryString);
            activity.SetTag("http.user_agent", context.Request.Headers.UserAgent.ToString());

            foreach (var header in _tagHeaders)
            {
                if (context.Request.Headers.TryGetValue(header, out var value))
                    activity.SetTag($"http.header.{header.ToLowerInvariant()}", value.ToString());
            }

            // Inject trace context into outbound headers for the proxied request
            var props = new PropagationContext(activity.Context, Baggage.Current);
            var propagator = Propagators.DefaultTextMapPropagator;

            propagator.Inject(props, context.Request.Headers, (headers, key, value) =>
            {
                headers[key] = value;
            });

            // Store activity in context for TelemetryEnd
            context.Items["__trace_activity"] = activity;
        }

        await next(context);
    }
}

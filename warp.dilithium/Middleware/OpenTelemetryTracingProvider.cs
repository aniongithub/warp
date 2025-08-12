using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using System.Diagnostics;

using Warp.Core;
using Warp.Core.Helper;

namespace Warp.Dilithium.Middleware;

public class OpenTelemetryTracingProvider : TracingProvider
{
    private static readonly ActivitySource ActivitySource = new("Warp");

    public OpenTelemetryTracingProvider(string name) : base(name) { }

    // Refactored: Always use Activity.Current as parent for in-process spans
    protected override TraceSpan CreateSpan(string traceParent)
    {
        // For in-process spans, always use Activity.Current as parent
        var activity = ActivitySource.StartActivity(Name, ActivityKind.Internal, Activity.Current?.Context ?? default);
        return new OpenTelemetryTraceSpan(activity);
    }

    protected override void OnDispose() { }
}

public class OpenTelemetryTraceSpan : TraceSpan
{
    private readonly Activity? _activity;

    public OpenTelemetryTraceSpan(Activity? activity)
    {
        _activity = activity;
        // ActivitySource.StartActivity already starts the activity, no need to call _activity?.Start()
    }

    protected override void OnSetTag(string key, string value)
    {
        _activity?.SetTag(key, value);
    }

    protected override void OnSetException(Exception ex)
    {
        _activity?.AddException(ex);
    }

    protected override void OnSetStatus(int status)
    {
        _activity?.SetStatus(status.IsErrorStatus() ? ActivityStatusCode.Error : ActivityStatusCode.Ok, $"HTTP {status.GetStatusDescription()}");
        _activity?.SetTag("http.status_code", status);
    }

    protected override void OnStop()
    {
        _activity?.Stop();
    }

    protected override void OnDispose()
    {
        _activity?.Dispose(); // Only dispose, do not stop again
    }
}

using Yarp.ReverseProxy.Forwarder;

public class TraceHeaderTransformer : HttpTransformer
{
    public static readonly TraceHeaderTransformer Instance = new();

    public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        // Call the base method to copy default headers and apply default transformations
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

        // Always set traceparent/tracestate from Activity.Current (the dispatch span)
        var activity = System.Diagnostics.Activity.Current;
        if (activity != null)
        {
            proxyRequest.Headers.Remove("traceparent");
            proxyRequest.Headers.TryAddWithoutValidation("traceparent", activity.Id);
            if (!string.IsNullOrEmpty(activity.TraceStateString))
            {
                proxyRequest.Headers.Remove("tracestate");
                proxyRequest.Headers.TryAddWithoutValidation("tracestate", activity.TraceStateString);
            }
        }
    }
}

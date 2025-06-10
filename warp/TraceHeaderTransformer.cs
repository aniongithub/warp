using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

public class TraceHeaderTransformProvider : ITransformProvider
{
    public void Apply(TransformBuilderContext context)
    {
        // Add a request transform to set traceparent/tracestate from Activity.Current
        context.AddRequestTransform(async transformContext =>
        {
            var activity = System.Diagnostics.Activity.Current;
            if (activity != null)
            {
                transformContext.ProxyRequest.Headers.Remove("traceparent");
                transformContext.ProxyRequest.Headers.TryAddWithoutValidation("traceparent", activity.Id);
                if (!string.IsNullOrEmpty(activity.TraceStateString))
                {
                    transformContext.ProxyRequest.Headers.Remove("tracestate");
                    transformContext.ProxyRequest.Headers.TryAddWithoutValidation("tracestate", activity.TraceStateString);
                }
            }
            await ValueTask.CompletedTask;
        });
    }

    public void ValidateCluster(TransformClusterValidationContext context) { }
    public void ValidateRoute(TransformRouteValidationContext context) { }
}
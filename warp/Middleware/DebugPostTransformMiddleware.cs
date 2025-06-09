using Yarp.ReverseProxy.Forwarder;

namespace Warp.Middleware
{
    public class PostTransformMiddlewareRunner : IPostTransformMiddleware
    {
        private readonly IDictionary<string, Func<RequestDelegate, RequestDelegate>> _componentMap;

        public PostTransformMiddlewareRunner(IDictionary<string, Func<RequestDelegate, RequestDelegate>> componentMap)
        {
            _componentMap = componentMap;
        }

        public async Task InvokeAsync(HttpContext context, HttpRequestMessage proxyRequest)
        {
            // Always set RequestPath to the current request path
            context.Items["RequestPath"] = context.Request.Path.Value ?? string.Empty;

            // If we have a transformed path, overwrite it for downstream middleware
            if (proxyRequest?.RequestUri != null)
                context.Items["RequestPath"] = proxyRequest.RequestUri.AbsolutePath;

            var proxyFeature = context.Features.Get<Yarp.ReverseProxy.Model.IReverseProxyFeature>();
            var metadata = proxyFeature?.Route?.Config?.Metadata;
            var predispatch = metadata != null && metadata.TryGetValue("Predispatch", out var pre) && pre is string preStr
                ? preStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Array.Empty<string>();
            if (predispatch.Length == 0)
                return;

            // Compose the pipeline for this request
            RequestDelegate terminal = _ => Task.CompletedTask;
            var pipeline = terminal;
            foreach (var name in predispatch.Reverse())
            {
                if (_componentMap.TryGetValue(name, out var middleware))
                {
                    var nextDelegate = pipeline;
                    pipeline = middleware(nextDelegate);
                }
            }
            await pipeline(context);
        }
    }
}

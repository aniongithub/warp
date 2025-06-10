using Yarp.ReverseProxy.Forwarder;

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

        var proxyFeature = context.Features.Get<Yarp.ReverseProxy.Model.IReverseProxyFeature>();
        var metadata = proxyFeature?.Route?.Config?.Metadata;
        var predispatch = metadata != null && metadata.TryGetValue("Predispatch", out var pre) && pre is string preStr
            ? preStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        if (predispatch.Length == 0)
            return;

        var destUri = new Uri(proxyFeature?.ProxiedDestination?.Model?.Config?.Address ?? "/");
        var hostPath = destUri.AbsolutePath.TrimEnd('/');
        var incomingPath = proxyRequest?.RequestUri?.AbsolutePath!;
        // Only strip if it's a prefix!
        string normalizedPath = incomingPath.StartsWith(hostPath, StringComparison.OrdinalIgnoreCase)
            ? incomingPath.Substring(hostPath.Length)
            : incomingPath;

        if (string.IsNullOrEmpty(normalizedPath))
            normalizedPath = "/";            

        // If we have a transformed path, overwrite it for downstream middleware
        if (proxyRequest?.RequestUri != null)
            context.Items["RequestPath"] = normalizedPath;

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

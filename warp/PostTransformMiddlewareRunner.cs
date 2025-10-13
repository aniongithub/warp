using Yarp.ReverseProxy.Forwarder;

public class PostTransformMiddlewareRunner : IPostTransformMiddleware
{
    private readonly Dictionary<string, List<Func<HttpContext, Func<Task>, Task>>> _middlewareCache;
    private readonly ILogger<PostTransformMiddlewareRunner> _logger;

    public PostTransformMiddlewareRunner(
        Dictionary<string, List<Func<HttpContext, Func<Task>, Task>>> middlewareCache,
        ILogger<PostTransformMiddlewareRunner> logger)
    {
        _middlewareCache = middlewareCache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, HttpRequestMessage proxyRequest)
    {
        // Always set RequestPath to the current request path
        context.Items["RequestPath"] = context.Request.Path.Value ?? string.Empty;

        var proxyFeature = context.Features.Get<Yarp.ReverseProxy.Model.IReverseProxyFeature>();
        var routeId = proxyFeature?.Route?.Config?.RouteId;
        
        if (string.IsNullOrEmpty(routeId))
            return;

        // Check if we have cached predispatch middleware for this route
        if (!_middlewareCache.TryGetValue($"{routeId}_predispatch", out var predispatchMiddleware))
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

        // Execute cached middleware functions
        foreach (var middlewareFunction in predispatchMiddleware)
        {
            await middlewareFunction(context, () => Task.CompletedTask);
        }
    }


}

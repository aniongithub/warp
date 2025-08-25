using Yarp.ReverseProxy.Forwarder;

public class PostTransformMiddlewareRunner : IPostTransformMiddleware
{
    private readonly IDictionary<string, Func<RequestDelegate, RequestDelegate>> _componentMap;
    private readonly Dictionary<string, Dictionary<string, string>> _routePhaseOverrides;
    private readonly ILogger<PostTransformMiddlewareRunner> _logger;

    public PostTransformMiddlewareRunner(
        IDictionary<string, Func<RequestDelegate, RequestDelegate>> componentMap,
        Dictionary<string, Dictionary<string, string>> routePhaseOverrides,
        ILogger<PostTransformMiddlewareRunner> logger)
    {
        _componentMap = componentMap;
        _routePhaseOverrides = routePhaseOverrides;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, HttpRequestMessage proxyRequest)
    {
        // Always set RequestPath to the current request path
        context.Items["RequestPath"] = context.Request.Path.Value ?? string.Empty;

        var proxyFeature = context.Features.Get<Yarp.ReverseProxy.Model.IReverseProxyFeature>();
        var metadata = proxyFeature?.Route?.Config?.Metadata;
        var routeId = proxyFeature?.Route?.Config?.RouteId;
        var predispatch = GetMiddlewareNames(metadata, "Predispatch", _routePhaseOverrides, routeId);
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

    private static string[] GetMiddlewareNames(IReadOnlyDictionary<string, string>? metadata, string phaseName, Dictionary<string, Dictionary<string, string>> routePhaseOverrides, string? routeId)
    {
        if (metadata == null)
            return Array.Empty<string>();

        // Check if we have a route-specific override
        if (!string.IsNullOrEmpty(routeId) && 
            routePhaseOverrides.TryGetValue(routeId, out var phaseOverrides) &&
            phaseOverrides.TryGetValue(phaseName, out var overrideValue))
        {
            return overrideValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        // Fall back to standard metadata
        if (metadata.TryGetValue(phaseName, out var phase))
        {
            return phase.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return Array.Empty<string>();
    }
}

using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using Warp.Core.Data;
using Warp.Core.Helper;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Load configuration from warp.yml
var configBuilder = new ConfigurationBuilder().AddWarpConfiguration("warp",
    baseDirectory: Environment.GetEnvironmentVariable("WARP_CONFIG_BASE_DIR") ?? "./config");
var config = configBuilder.Build();

// Use the extension method to create the DataContext from configuration
var dataContext = config.GetSection("DataContext").CreateFromConfiguration();
builder.Services.AddSingleton(dataContext);
builder.Services.AddRequestTimeouts();

// Load inline middleware definitions from routes - will cache them after app is built
var routesSection = config.GetSection("ReverseProxy:Routes");

// Register YARP reverse proxy from config
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(config.GetSection("ReverseProxy"));

// Always configure OpenTelemetry for unified tracing
var tempLogger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger("Startup");
tempLogger.LogInformation("Configuring OpenTelemetry for unified tracing...");

var otelSection = config.GetSection("OpenTelemetry");
var sourceNames = otelSection.GetSection("SourceNames").Get<string[]>() ?? new[] { "Warp" };
var otelEndpoint = otelSection.GetValue<string>("Endpoint") ?? "http://localhost:4317";
var serviceName = otelSection.GetValue<string>("ServiceName") ?? "Warp";

builder.Services.AddOpenTelemetry().WithTracing(tracer =>
{
    tracer
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
        .AddAspNetCoreInstrumentation(options =>
            options.EnrichWithHttpResponse = (activity, response) =>
                activity.SetStatus(response?.StatusCode.IsErrorStatus() == true
                    ? ActivityStatusCode.Error
                    : ActivityStatusCode.Ok,
                    response != null
                        ? $"HTTP {response.StatusCode.GetStatusDescription()}"
                        : string.Empty))
        .AddSource(sourceNames)
        .AddConsoleExporter() // For hierarchical console logging
        .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otelEndpoint)) // For Jaeger/external tools
        .AddHttpClientInstrumentation();
});

// Now build the app
var app = builder.Build();
app.UseRequestTimeouts();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

// Pre-build and cache middleware for each route and phase
var middlewareCache = new Dictionary<string, List<Func<HttpContext, Func<Task>, Task<bool>>>>();
foreach (var route in routesSection.GetChildren())
{
    var routeId = route.Key;
    
    // Cache Preprocess middleware
    var preprocessSection = route.GetSection("Metadata:Preprocess");
    if (preprocessSection.Exists())
    {
        middlewareCache[$"{routeId}_preprocess"] = 
            Warp.Core.Extensions.MiddlewarePipelineExtensions.CreateMiddlewareFromConfig(
                preprocessSection, app.Services, $"{routeId}_preprocess");
    }
    
    // Cache Predispatch middleware
    var predispatchSection = route.GetSection("Metadata:Predispatch");
    if (predispatchSection.Exists())
    {
        middlewareCache[$"{routeId}_predispatch"] = 
            Warp.Core.Extensions.MiddlewarePipelineExtensions.CreateMiddlewareFromConfig(
                predispatchSection, app.Services, $"{routeId}_predispatch");
    }
    
    // Cache Postdispatch middleware
    var postdispatchSection = route.GetSection("Metadata:Postdispatch");
    if (postdispatchSection.Exists())
    {
        middlewareCache[$"{routeId}_postdispatch"] = 
            Warp.Core.Extensions.MiddlewarePipelineExtensions.CreateMiddlewareFromConfig(
                postdispatchSection, app.Services, $"{routeId}_postdispatch");
    }
    
    // Cache Postprocess middleware
    var postprocessSection = route.GetSection("Metadata:Postprocess");
    if (postprocessSection.Exists())
    {
        middlewareCache[$"{routeId}_postprocess"] = 
            Warp.Core.Extensions.MiddlewarePipelineExtensions.CreateMiddlewareFromConfig(
                postprocessSection, app.Services, $"{routeId}_postprocess");
    }
}

// Configure CORS only if explicitly configured - no insecure defaults
var corsSection = config.GetSection("Cors");
if (corsSection.Exists())
{
    var allowedOrigins = corsSection.GetSection("AllowedOrigins").Get<string[]>();
    var allowedMethods = corsSection.GetSection("AllowedMethods").Get<string[]>();
    var allowedHeaders = corsSection.GetSection("AllowedHeaders").Get<string[]>();
    var allowCredentials = corsSection.GetValue<bool>("AllowCredentials");

    if (allowedOrigins?.Length > 0)
    {
        logger.LogInformation("Configuring CORS with {OriginCount} allowed origins", allowedOrigins.Length);
        
        app.UseCors(policy =>
        {
            policy.WithOrigins(allowedOrigins);
            
            if (allowedMethods?.Length > 0)
                policy.WithMethods(allowedMethods);
            else
                policy.AllowAnyMethod();
            
            if (allowedHeaders?.Length > 0)
                policy.WithHeaders(allowedHeaders);
            else
                policy.AllowAnyHeader();
            
            if (allowCredentials)
                policy.AllowCredentials();
        });
    }
    else
    {
        logger.LogWarning("CORS section found but no AllowedOrigins specified - CORS not configured");
    }
}
else
{
    logger.LogInformation("No CORS configuration found - CORS not enabled");
}

// YARP per-route middleware using MapReverseProxy with extension methods
app.MapReverseProxy(proxyPipeline =>
{
    // PREPROCESS: runs before YARP transforms
    proxyPipeline.Use(async (context, next) =>
    {
        var proxyFeature = context.Features.Get<Yarp.ReverseProxy.Model.IReverseProxyFeature>();
        var routeId = proxyFeature?.Route?.Config?.RouteId;
        
        if (!string.IsNullOrEmpty(routeId) && middlewareCache.TryGetValue($"{routeId}_preprocess", out var preprocessMiddleware))
        {
            foreach (var middlewareFunction in preprocessMiddleware)
            {
                var shouldContinue = await middlewareFunction(context, () => Task.CompletedTask);
                if (!shouldContinue)
                {
                    // Middleware has handled the response, stop pipeline
                    return;
                }
            }
        }
        
        await next(context);
    });

    // PREDISPATCH: runs after YARP's config-based transforms but before dispatch to the
    // backend. We apply the route's transform engine to a throwaway request message so we
    // can read the transformed path (exposed via context.Items["RequestPath"]) without
    // dispatching, then run the predispatch middleware. If a predispatch middleware
    // short-circuits (returns false), we skip dispatch so it can return its own response.
    // This replaces the previous IPostTransformMiddleware hook that required a custom YARP fork.
    proxyPipeline.Use(async (context, next) =>
    {
        var proxyFeature = context.Features.Get<Yarp.ReverseProxy.Model.IReverseProxyFeature>();
        var routeId = proxyFeature?.Route?.Config?.RouteId;

        // Default RequestPath to the current (untransformed) path for downstream consumers.
        context.Items["RequestPath"] = context.Request.Path.Value ?? string.Empty;

        if (string.IsNullOrEmpty(routeId) ||
            !middlewareCache.TryGetValue($"{routeId}_predispatch", out var predispatchMiddleware))
        {
            await next(context);
            return;
        }

        // Apply the route's YARP transforms to a throwaway HttpRequestMessage to obtain the
        // transformed path without forwarding. Transforms only mutate the outgoing request
        // (not the HttpContext), so running them here and again during the real dispatch is
        // idempotent for the config-based path/header transforms Warp uses.
        var transformer = proxyFeature?.Route?.Transformer;
        if (transformer != null)
        {
            // Use the cluster's destination address as the prefix so we can strip the backend
            // base path afterwards, matching the outgoing request URI YARP will build.
            var destinationAddress = proxyFeature?.Cluster?.Config?.Destinations?.Values
                .FirstOrDefault()?.Address;
            if (string.IsNullOrEmpty(destinationAddress))
                destinationAddress = "http://placeholder";

            using var throwaway = new HttpRequestMessage();
            await transformer.TransformRequestAsync(context, throwaway, destinationAddress, context.RequestAborted);

            if (throwaway.RequestUri != null)
            {
                var destUri = new Uri(destinationAddress);
                var hostPath = destUri.AbsolutePath.TrimEnd('/');
                var transformedPath = throwaway.RequestUri.IsAbsoluteUri
                    ? throwaway.RequestUri.AbsolutePath
                    : throwaway.RequestUri.OriginalString;

                var normalizedPath = !string.IsNullOrEmpty(hostPath) &&
                    transformedPath.StartsWith(hostPath, StringComparison.OrdinalIgnoreCase)
                        ? transformedPath.Substring(hostPath.Length)
                        : transformedPath;

                if (string.IsNullOrEmpty(normalizedPath))
                    normalizedPath = "/";

                context.Items["RequestPath"] = normalizedPath;
            }
        }

        // Run the cached predispatch middleware. Each returns whether the pipeline should
        // continue; if one short-circuits (returns false) it has handled the response, so we
        // skip dispatch to the backend entirely.
        foreach (var middlewareFunction in predispatchMiddleware)
        {
            var shouldContinue = await middlewareFunction(context, () => Task.CompletedTask);
            if (!shouldContinue)
            {
                // Middleware has handled the response, prevent dispatch
                return;
            }
        }

        await next(context);
    });

    // POSTDISPATCH: runs after dispatch, before postprocess
    proxyPipeline.Use(async (context, next) =>
    {
        // Call next first to get the response from the backend
        await next();
        
        var proxyFeature = context.Features.Get<Yarp.ReverseProxy.Model.IReverseProxyFeature>();
        var routeId = proxyFeature?.Route?.Config?.RouteId;
        
        if (!string.IsNullOrEmpty(routeId) && middlewareCache.TryGetValue($"{routeId}_postdispatch", out var postdispatchMiddleware))
        {
            foreach (var middlewareFunction in postdispatchMiddleware)
            {
                var shouldContinue = await middlewareFunction(context, () => Task.CompletedTask);
                if (!shouldContinue)
                {
                    // Middleware has handled the response, stop pipeline
                    break;
                }
            }
        }
    });

    // POSTPROCESS: runs last
    proxyPipeline.Use(async (context, next) =>
    {
        await next();
        
        var proxyFeature = context.Features.Get<Yarp.ReverseProxy.Model.IReverseProxyFeature>();
        var routeId = proxyFeature?.Route?.Config?.RouteId;
        
        if (!string.IsNullOrEmpty(routeId) && middlewareCache.TryGetValue($"{routeId}_postprocess", out var postprocessMiddleware))
        {
            foreach (var middlewareFunction in postprocessMiddleware)
            {
                var shouldContinue = await middlewareFunction(context, () => Task.CompletedTask);
                if (!shouldContinue)
                {
                    // Middleware has handled the response, stop pipeline
                    break;
                }
            }
        }
    });
});

var appLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
appLogger.LogInformation("Warp startup complete. Ready to accept requests.");

app.Run();

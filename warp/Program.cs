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

// Register PostTransformMiddlewareRunner for YARP post-transform (predispatch) extensibility
// We use a factory approach since we need the middleware cache which is built later
builder.Services.AddSingleton<Yarp.ReverseProxy.Forwarder.IPostTransformMiddleware>(serviceProvider =>
{
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<PostTransformMiddlewareRunner>();
    
    // Create middleware cache on-demand
    var middlewareCache = new Dictionary<string, List<Func<HttpContext, Func<Task>, Task<bool>>>>();
    foreach (var route in routesSection.GetChildren())
    {
        var routeId = route.Key;
        var predispatchSection = route.GetSection("Metadata:Predispatch");
        if (predispatchSection.Exists())
        {
            middlewareCache[$"{routeId}_predispatch"] = 
                Warp.Core.Extensions.MiddlewarePipelineExtensions.CreateMiddlewareFromConfig(
                    predispatchSection, serviceProvider, $"{routeId}_predispatch");
        }
    }
    
    return new PostTransformMiddlewareRunner(middlewareCache, logger);
});

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

    // PREDISPATCH: handled by PostTransformMiddlewareRunner (registered as IPostTransformMiddleware)

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

using OpenTelemetry.Trace;
using OpenTelemetry.Resources;

using Warp;
using Warp.Core.Data;
using Warp.Core.Middleware;
using Warp.Core.Helper;
using Warp.Dilithium.Middleware;
using System.Diagnostics;
using Microsoft.VisualBasic;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// Load configuration from warp.yml
var configBuilder = new ConfigurationBuilder().AddWarpConfiguration("warp",
    baseDirectory: Environment.GetEnvironmentVariable("WARP_CONFIG_BASE_DIR") ?? "./config");
var config = configBuilder.Build();

// Use the extension method to create the DataContext from configuration
var dataContext = config.GetSection("DataContext").CreateFromConfiguration();
builder.Services.AddSingleton(dataContext);
builder.Services.AddRequestTimeouts();

// Load pipeline definitions from both PipelineComponents and inline route definitions
var pipelineSection = config.GetSection("PipelineComponents");
var pipelineComponents = new List<MiddlewareDescriptor>();
var routePhaseOverrides = new Dictionary<string, Dictionary<string, string>>(); // routeId -> phase -> middleware list

// Load global pipeline components (for backward compatibility)
foreach (var section in pipelineSection.GetChildren())
{
    var descriptor = new MiddlewareDescriptor
    {
        Name = section.GetValue<string>("Name")!,
        Type = section.GetValue<string>("Type")!,
        Options = section.GetSection("Options")
    };
    pipelineComponents.Add(descriptor);
}

// Load inline middleware definitions from routes
var routesSection = config.GetSection("ReverseProxy:Routes");
foreach (var routeSection in routesSection.GetChildren())
{
    var routeId = routeSection.Key;
    var metadataSection = routeSection.GetSection("Metadata");
    
    foreach (var phaseSection in metadataSection.GetChildren())
    {
        var phaseName = phaseSection.Key;
        if (phaseName is "Preprocess" or "Predispatch" or "Postdispatch" or "Postprocess")
        {
            // Check if this phase has inline middleware definitions (dict format)
            if (phaseSection.GetChildren().Any(child => child.GetChildren().Any()))
            {
                // Dictionary format - load middleware definitions
                var middlewareNames = new List<string>();
                foreach (var middlewareSection in phaseSection.GetChildren())
                {
                    var middlewareName = middlewareSection.Key;
                    var middlewareType = middlewareSection.GetValue<string>("Type");
                    
                    if (!string.IsNullOrEmpty(middlewareType))
                    {
                        var descriptor = new MiddlewareDescriptor
                        {
                            Name = middlewareName,
                            Type = middlewareType,
                            Options = middlewareSection.GetSection("Options")
                        };
                        
                        // Only add if not already defined globally
                        if (!pipelineComponents.Any(pc => pc.Name == middlewareName))
                        {
                            pipelineComponents.Add(descriptor);
                        }
                        
                        middlewareNames.Add(middlewareName);
                    }
                }
                
                // Store the override for this route and phase
                if (middlewareNames.Count > 0)
                {
                    if (!routePhaseOverrides.ContainsKey(routeId))
                        routePhaseOverrides[routeId] = new Dictionary<string, string>();
                    
                    routePhaseOverrides[routeId][phaseName] = string.Join(",", middlewareNames);
                }
            }
        }
    }
}

// Register YARP reverse proxy from config
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(config.GetSection("ReverseProxy"));

// Build a map of pipeline components (declare before using block)
var componentMap = new Dictionary<string, Func<RequestDelegate, RequestDelegate>>();
// Build a temporary service provider for setup
#pragma warning disable ASP0000 // We need to do this to resolve middleware types before app build
using (var tempProvider = builder.Services.BuildServiceProvider())
{
    var tempLoggerFactory = tempProvider.GetRequiredService<ILoggerFactory>();
    var tempLogger = tempLoggerFactory.CreateLogger("Startup");

    var needsOtel = pipelineComponents.Any(pc =>
    pc.Type.Contains("OpenTelemetry", StringComparison.OrdinalIgnoreCase));

    // Populate componentMap
    foreach (var descriptor in pipelineComponents)
    {
        tempLogger.LogInformation("Registering middleware: {Name} ({Type})", descriptor.Name, descriptor.Type);
        try
        {
            tempLogger.LogDebug("Resolving middleware type: {Type}", descriptor.Type);
            var middlewareType = descriptor.Type.ResolveType()
                ?? throw new Exception($"Could not find middleware type: {descriptor.Type}");
            tempLogger.LogDebug("Checking inheritance chain for middleware: {MiddlewareType}", middlewareType.FullName);
            
            // Find the MiddlewareBase<> in the inheritance chain
            Type? configBaseType = null;
            var currentType = middlewareType;
            while (currentType != null && currentType != typeof(object))
            {
                if (currentType.IsGenericType && currentType.GetGenericTypeDefinition() == typeof(MiddlewareBase<>))
                {
                    configBaseType = currentType;
                    break;
                }
                currentType = currentType.BaseType;
            }
            
            if (configBaseType == null)
                throw new Exception($"Middleware type {middlewareType.FullName} does not inherit from MiddlewareBase<>.");
            
            tempLogger.LogDebug("Resolving configuration type for middleware: {MiddlewareType}", middlewareType.FullName);
            var configType = configBaseType.GetGenericArguments()[0];
            var configInstance = Activator.CreateInstance(configType);


            if (descriptor.Options != null)
            {
                var is_otel_middleware = descriptor.Type.StartsWith("Warp.Middleware.OpenTelemetry", StringComparison.OrdinalIgnoreCase);
                if (needsOtel && !is_otel_middleware)
                {
                    descriptor.Options["TracingEnabled"] = "true";
                    descriptor.Options["TracingProvider"] = "Warp.Dilithium.Middleware.OpenTelemetryTracingProvider, warp.dilithium";
                    descriptor.Options["TraceName"] = $"{descriptor.Name}.Trace";
                }
                tempLogger.LogDebug("Binding options for middleware: {Name}", descriptor.Name);
                descriptor.Options.Bind(configInstance); // Assuming Options is IConfigurationSection
            }
            if (configInstance == null)
            {
                tempLogger.LogError("Configuration instance for middleware {Name} is null.", descriptor.Name);
                throw new Exception($"Configuration instance for middleware {descriptor.Name} is null.");
            }

            tempLogger.LogDebug("Creating middleware instance for: {Name}", descriptor.Name);
            var loggerInstance = tempLoggerFactory.CreateLogger(descriptor.Name);
            var middleware = ActivatorUtilities.CreateInstance(tempProvider, middlewareType, descriptor.Name, loggerInstance, dataContext!, configInstance)
                ?? throw new Exception($"Could not create middleware {descriptor.Name}");
            tempLogger.LogInformation("Successfully registered middleware: {Name}", descriptor.Name);
            componentMap[descriptor.Name] = next => async context =>
            {
                var method = middlewareType.GetMethod("InvokeWithTracingAsync");
                var task = method?.Invoke(middleware, new object[] { context, next }) as Task;
                if (task == null)
                    throw new InvalidOperationException($"{descriptor.Name} did not return a valid Task");
                await task;
            };
        }
        catch (Exception)
        {
            tempLogger.LogError("Failed to register middleware: {Name} ({Type})", descriptor.Name, descriptor.Type);
            throw;
        }
    }

    if (needsOtel)
    {
        tempLogger.LogInformation("OpenTelemetry middleware detected, configuring OpenTelemetry...");

        // Ensure we use the correct config object, not builder.Configuration
        var otelSection = config.GetSection("OpenTelemetry");

        var sourceNames = otelSection.GetSection("SourceNames").Get<string[]>() ?? new[] { "Warp" };

        // Ensure all routes are traced if OpenTelemetry is enabled
        var routes = config.GetSection("ReverseProxy:Routes").Get<List<RouteDescriptor>>() ?? [];
        foreach (var route in routes)
        {
            // Set tracing properties directly in the configuration section if possible
            var routeType = route.GetType();
            var tracingEnabledProp = routeType.GetProperty("TracingEnabled");
            if (tracingEnabledProp != null && tracingEnabledProp.CanWrite)
                tracingEnabledProp.SetValue(route, true);
            var tracingProviderProp = routeType.GetProperty("TracingProvider");
            if (tracingProviderProp != null && tracingProviderProp.CanWrite)
                tracingProviderProp.SetValue(route, "Warp.Middleware.OpenTelemetryTracingProvider, Warp");
            var traceNameProp = routeType.GetProperty("TraceName");
            if (traceNameProp != null && traceNameProp.CanWrite)
                traceNameProp.SetValue(route, $"{route.Cluster}.{route.Path}");
        }

        // Optionally register OpenTelemetry if configured
        if (sourceNames is { Length: > 0 })
        {
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
                    .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otelEndpoint))
                    .AddHttpClientInstrumentation();
            });
        }
    }
    else
        tempLogger.LogInformation("No OpenTelemetry middleware detected, skipping OpenTelemetry configuration.");
        
    // Register PostTransformMiddlewareRunner for YARP post-transform (predispatch) extensibility
    builder.Services.AddSingleton<Yarp.ReverseProxy.Forwarder.IPostTransformMiddleware>(
        sp => new PostTransformMiddlewareRunner(componentMap, routePhaseOverrides, sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostTransformMiddlewareRunner>())
    );
}
#pragma warning restore ASP0000 // Do not call 'IServiceCollection.BuildServiceProvider' in 'ConfigureServices'

// Helper method to extract middleware names from metadata (supports both string and dict formats)
static string[] GetMiddlewareNames(IReadOnlyDictionary<string, string>? metadata, string phaseName, Dictionary<string, Dictionary<string, string>> routePhaseOverrides, string? routeId = null)
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

// Now build the app
var app = builder.Build();
app.UseRequestTimeouts();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

app.UseCors(policy => policy
    .WithOrigins("http://localhost:3030")
    .AllowAnyMethod()
    .AllowAnyHeader()
    .AllowCredentials()
);

// YARP per-route middleware using MapReverseProxy
app.MapReverseProxy(proxyPipeline =>
{
    // PREPROCESS: runs before YARP transforms
    proxyPipeline.Use(async (context, next) =>
    {
        var proxyFeature = context.Features.Get<Yarp.ReverseProxy.Model.IReverseProxyFeature>();
        var metadata = proxyFeature?.Route?.Config?.Metadata;
        var routeId = proxyFeature?.Route?.Config?.RouteId;
        var preprocess = GetMiddlewareNames(metadata, "Preprocess", routePhaseOverrides, routeId);
        foreach (var name in preprocess.Reverse())
        {
            if (componentMap.TryGetValue(name, out var middleware))
            {
                var nextDelegate = next;
                next = ctx => middleware(nextDelegate)(ctx);
            }
        }
        await next(context);
    });

    // PREDISPATCH: handled by IPostTransformMiddleware (PostTransformMiddlewareRunner)
    // No manual block here; handled by YARP extensibility

    // POSTDISPATCH: runs after dispatch, before postprocess
    proxyPipeline.Use(async (context, next) =>
    {
        await next();
        var proxyFeature = context.Features.Get<Yarp.ReverseProxy.Model.IReverseProxyFeature>();
        var metadata = proxyFeature?.Route?.Config?.Metadata;
        var routeId = proxyFeature?.Route?.Config?.RouteId;
        var postdispatch = GetMiddlewareNames(metadata, "Postdispatch", routePhaseOverrides, routeId);
        foreach (var name in postdispatch)
        {
            if (componentMap.TryGetValue(name, out var middleware))
            {
                await middleware(_ => Task.CompletedTask)(context);
            }
        }
    });

    // POSTPROCESS: runs last
    proxyPipeline.Use(async (context, next) =>
    {
        await next();
        var proxyFeature = context.Features.Get<Yarp.ReverseProxy.Model.IReverseProxyFeature>();
        var metadata = proxyFeature?.Route?.Config?.Metadata;
        var routeId = proxyFeature?.Route?.Config?.RouteId;
        var postprocess = GetMiddlewareNames(metadata, "Postprocess", routePhaseOverrides, routeId);
        foreach (var name in postprocess)
        {
            if (componentMap.TryGetValue(name, out var middleware))
            {
                await middleware(_ => Task.CompletedTask)(context);
            }
        }
    });
});

logger.LogInformation("Warp core startup complete. Ready to accept requests.");

app.Run();

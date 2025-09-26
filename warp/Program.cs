using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using Warp.Core.Data;
using Warp.Core.Middleware;
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

// Load inline middleware definitions from routes
var routesSection = config.GetSection("ReverseProxy:Routes");
var pipelineComponents = new List<MiddlewareDescriptor>();
var routePhaseOverrides = new Dictionary<string, Dictionary<string, string>>(); // routeId -> phase -> middleware list
foreach (var routeSection in routesSection.GetChildren())
{
    var routeId = routeSection.Key;
    var metadataSection = routeSection.GetSection("Metadata");
    
    foreach (var phaseSection in metadataSection.GetChildren())
    {
        var phaseName = phaseSection.Key;
        if (phaseName is "Preprocess" or "Predispatch" or "Postdispatch" or "Postprocess")
        {
            // Check if this phase has inline middleware definitions (array format)
            var middlewareArray = phaseSection.Get<object[]>();
            if (middlewareArray != null && middlewareArray.Length > 0)
            {
                var middlewareNames = new List<string>();
                
                for (int i = 0; i < middlewareArray.Length; i++)
                {
                    var middlewareConfig = phaseSection.GetSection($"{i}");
                    var middlewareType = middlewareConfig.GetValue<string>("Type");
                    
                    if (!string.IsNullOrEmpty(middlewareType))
                    {
                        // Generate a unique name for this middleware instance
                        var middlewareName = $"{routeId}_{phaseName}_{i}_{middlewareType.Split('.').Last().Split(',').First()}";
                        
                        var descriptor = new MiddlewareDescriptor
                        {
                            Name = middlewareName,
                            Type = middlewareType,
                            Options = middlewareConfig.GetSection("Options")
                        };
                        
                        pipelineComponents.Add(descriptor);
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
            
            // Find the MiddlewareBase<> in the inheritance chain so we can get the options type
            var configBaseType = middlewareType.GetMiddlewareBaseType();
            
            if (configBaseType == null)
                throw new Exception($"Middleware type {middlewareType.FullName} does not inherit from MiddlewareBase<>.");
            
            tempLogger.LogDebug("Resolving configuration type for middleware: {MiddlewareType}", middlewareType.FullName);
            var configType = configBaseType.GetGenericArguments()[0];
            var configInstance = Activator.CreateInstance(configType);


            if (descriptor.Options != null)
            {
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

    // Always configure OpenTelemetry for unified tracing
    tempLogger.LogInformation("Configuring OpenTelemetry for unified tracing...");

    // Ensure we use the correct config object, not builder.Configuration
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
        
    // Register PostTransformMiddlewareRunner for YARP post-transform (predispatch) extensibility
    builder.Services.AddSingleton<Yarp.ReverseProxy.Forwarder.IPostTransformMiddleware>(
        sp => new PostTransformMiddlewareRunner(componentMap, routePhaseOverrides, sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostTransformMiddlewareRunner>())
    );
}
#pragma warning restore ASP0000 // Do not call 'IServiceCollection.BuildServiceProvider' in 'ConfigureServices'

// Helper method to extract middleware names from route phase overrides
static string[] GetMiddlewareNames(IReadOnlyDictionary<string, string>? metadata, string phaseName, Dictionary<string, Dictionary<string, string>> routePhaseOverrides, string? routeId = null)
{
    // Only check route-specific overrides since we no longer support global metadata
    if (!string.IsNullOrEmpty(routeId) && 
        routePhaseOverrides.TryGetValue(routeId, out var phaseOverrides) &&
        phaseOverrides.TryGetValue(phaseName, out var overrideValue))
    {
        return overrideValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    return Array.Empty<string>();
}

// Now build the app
var app = builder.Build();
app.UseRequestTimeouts();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

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
        var proxyFeature = context.Features.Get<Yarp.ReverseProxy.Model.IReverseProxyFeature>();
        var metadata = proxyFeature?.Route?.Config?.Metadata;
        var routeId = proxyFeature?.Route?.Config?.RouteId;
        var postdispatch = GetMiddlewareNames(metadata, "Postdispatch", routePhaseOverrides, routeId);
        
        if (postdispatch.Length == 0)
        {
            await next();
            return;
        }

        // Intercept the response stream to allow middlewares to process the response
        var originalBodyStream = context.Response.Body;
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        try
        {
            // Call next to get the response from the backend
            await next();

            // Reset stream position to read the response
            responseBody.Seek(0, SeekOrigin.Begin);

            // Now run postdispatch middlewares with the response available
            foreach (var name in postdispatch)
            {
                if (componentMap.TryGetValue(name, out var middleware))
                {
                    await middleware(_ => Task.CompletedTask)(context);
                }
            }

            // Copy the response back to the original stream
            responseBody.Seek(0, SeekOrigin.Begin);
            await responseBody.CopyToAsync(originalBodyStream);
        }
        finally
        {
            context.Response.Body = originalBodyStream;
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

logger.LogInformation("Warp startup complete. Ready to accept requests.");

app.Run();

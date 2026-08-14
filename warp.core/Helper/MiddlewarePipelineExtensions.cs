using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warp.Core.Data;
using Warp.Core.Helper;
using Warp.Core.Middleware;

namespace Warp.Core.Extensions;

public static class MiddlewarePipelineExtensions
{
    /// <summary>
    /// Creates middleware functions from configuration that can be executed directly.
    /// </summary>
    /// <param name="middlewareSection">Configuration section containing middleware definitions</param>
    /// <param name="serviceProvider">Service provider for dependency injection</param>
    /// <param name="namePrefix">Prefix for middleware instance names (for uniqueness)</param>
    /// <returns>A list of middleware functions</returns>
    public static List<Func<HttpContext, Func<Task>, Task<bool>>> CreateMiddlewareFromConfig(
        IConfigurationSection middlewareSection,
        IServiceProvider serviceProvider,
        string namePrefix = "middleware")
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("MiddlewarePipelineExtensions");
        var dataContext = serviceProvider.GetRequiredService<IDataContext>();
        
        var middlewareFunctions = new List<Func<HttpContext, Func<Task>, Task<bool>>>();
        
        // Parse middleware array from configuration
        var middlewareArray = middlewareSection.Get<object[]>();
        if (middlewareArray == null || middlewareArray.Length == 0)
        {
            logger.LogDebug("No middleware found in configuration section");
            return middlewareFunctions;
        }

        for (int i = 0; i < middlewareArray.Length; i++)
        {
            var middlewareConfig = middlewareSection.GetSection($"{i}");
            var middlewareType = middlewareConfig.GetValue<string>("Type");
            
            if (string.IsNullOrEmpty(middlewareType))
            {
                logger.LogWarning("Skipping middleware at index {Index} - no Type specified", i);
                continue;
            }

            // Generate a unique name for this middleware instance
            var middlewareName = $"{namePrefix}_{i}_{middlewareType.Split('.').Last().Split(',').First()}";
            
            try
            {
                logger.LogInformation("Creating middleware: {Name} ({Type})", middlewareName, middlewareType);
                
                // Resolve middleware type
                var type = middlewareType.ResolveType()
                    ?? throw new Exception($"Could not find middleware type: {middlewareType}");
                
                // Find the MiddlewareBase<> in the inheritance chain to get the options type
                var configBaseType = type.GetMiddlewareBaseType();
                if (configBaseType == null)
                    throw new Exception($"Middleware type {type.FullName} does not inherit from MiddlewareBase<>.");
                
                // Create and bind configuration instance
                var configType = configBaseType.GetGenericArguments()[0];
                var configInstance = Activator.CreateInstance(configType);
                
                var optionsSection = middlewareConfig.GetSection("Options");
                if (optionsSection.Exists() && configInstance != null)
                {
                    logger.LogDebug("Binding options for middleware: {Name}", middlewareName);
                    optionsSection.Bind(configInstance);
                }
                
                if (configInstance == null)
                {
                    logger.LogError("Configuration instance for middleware {Name} is null", middlewareName);
                    throw new Exception($"Configuration instance for middleware {middlewareName} is null.");
                }

                // Create middleware instance
                var middlewareLogger = loggerFactory.CreateLogger(middlewareName);
                var middleware = ActivatorUtilities.CreateInstance(serviceProvider, type, middlewareName, middlewareLogger, dataContext, configInstance)
                    ?? throw new Exception($"Could not create middleware {middlewareName}");
                
                // Cast to the strongly-typed interface once at build time so the per-request
                // delegate can invoke InvokeWithTracingAsync directly, with no reflection on the
                // hot path. All Warp middleware derive from MiddlewareBase<> which implements
                // IWarpMiddleware, so this cast is expected to succeed.
                if (middleware is IWarpMiddleware warpMiddleware)
                {
                    middlewareFunctions.Add(async (context, next) =>
                    {
                        var result = await warpMiddleware.InvokeWithTracingAsync(
                            context, new RequestDelegate(_ => next()));
                        
                        // Return whether pipeline should continue
                        return result is Result warpResult && warpResult.Action == PipelineAction.Continue;
                    });
                }
                else
                {
                    // Safe fallback for a type that does not implement IWarpMiddleware. This is
                    // logged once here at startup; the MethodInfo is resolved once (not per
                    // request) and reused by the closure.
                    logger.LogWarning(
                        "Middleware {Name} ({Type}) does not implement IWarpMiddleware; falling back to reflective invocation.",
                        middlewareName, middlewareType);
                    
                    var method = type.GetMethod("InvokeWithTracingAsync")
                        ?? throw new InvalidOperationException(
                            $"{middlewareName} does not expose an InvokeWithTracingAsync method");
                    
                    middlewareFunctions.Add(async (context, next) =>
                    {
                        var task = method.Invoke(middleware, new object[] { context, new RequestDelegate(_ => next()) }) as Task<IResult>;
                        if (task == null)
                            throw new InvalidOperationException($"{middlewareName} did not return a valid Task<IResult>");
                        
                        var result = await task;
                        
                        // Return whether pipeline should continue
                        return result is Result warpResult && warpResult.Action == PipelineAction.Continue;
                    });
                }
                
                logger.LogInformation("Successfully created middleware: {Name}", middlewareName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create middleware: {Name} ({Type})", middlewareName, middlewareType);
                throw;
            }
        }
        
        return middlewareFunctions;
    }

    /// <summary>
    /// Adds middleware to the application pipeline based on configuration.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <param name="middlewareSection">Configuration section containing middleware definitions</param>
    /// <param name="namePrefix">Prefix for middleware instance names (for uniqueness)</param>
    /// <returns>A list of middleware instance names that were created</returns>
    public static List<string> AddMiddlewareFromConfig(
        this IApplicationBuilder app,
        IConfigurationSection middlewareSection,
        string namePrefix = "middleware")
    {
        var middlewareFunctions = CreateMiddlewareFromConfig(middlewareSection, app.ApplicationServices, namePrefix);
        var middlewareNames = new List<string>();
        
        for (int i = 0; i < middlewareFunctions.Count; i++)
        {
            var middlewareFunction = middlewareFunctions[i];
            var middlewareName = $"{namePrefix}_{i}";
            
            app.Use(async (context, next) =>
            {
                await middlewareFunction(context, () => next());
            });
            
            middlewareNames.Add(middlewareName);
        }
        
        return middlewareNames;
    }
}
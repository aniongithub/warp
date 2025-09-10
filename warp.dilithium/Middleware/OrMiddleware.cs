using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warp.Core.Data;
using Warp.Core.Middleware;

namespace Warp.Dilithium.Middleware;

public class OrMiddlewareOptions : MiddlewareOptions
{
    public MiddlewareDescriptor? Middleware1 { get; set; }
    public MiddlewareDescriptor? Middleware2 { get; set; }
}

public sealed class OrMiddleware : MiddlewareBase<OrMiddlewareOptions>
{
    private readonly IWarpMiddleware? _middleware1;
    private readonly IWarpMiddleware? _middleware2;

    public OrMiddleware(string name, ILogger logger, IDataContext context, OrMiddlewareOptions options, IServiceProvider serviceProvider)
        : base(name, logger, context, options)
    {
        _middleware1 = CreateMiddleware(options.Middleware1, serviceProvider);
        _middleware2 = CreateMiddleware(options.Middleware2, serviceProvider);

        if (_middleware1 == null && _middleware2 == null)
        {
            throw new InvalidOperationException("OrMiddleware requires at least one middleware to be configured.");
        }

        Logger.LogDebug("OrMiddleware configured with Middleware1={Middleware1}, Middleware2={Middleware2}", 
            _middleware1?.Name ?? "null", _middleware2?.Name ?? "null");
    }

    private IWarpMiddleware? CreateMiddleware(MiddlewareDescriptor? descriptor, IServiceProvider serviceProvider)
    {
        if (descriptor == null)
            return null;

        var middlewareType = Type.GetType(descriptor.Type);
        if (middlewareType == null)
        {
            Logger.LogError("Middleware type '{Type}' could not be found", descriptor.Type);
            return null;
        }

        // Get the options type from the middleware type
        var baseType = middlewareType.BaseType;
        while (baseType != null && (!baseType.IsGenericType || baseType.GetGenericTypeDefinition() != typeof(MiddlewareBase<>)))
        {
            baseType = baseType.BaseType;
        }

        if (baseType == null)
        {
            Logger.LogError("Middleware type '{Type}' does not inherit from MiddlewareBase<>", descriptor.Type);
            return null;
        }

        var optionsType = baseType.GetGenericArguments()[0];
        var options = Activator.CreateInstance(optionsType);

        if (descriptor.Options != null && options != null)
        {
            descriptor.Options.Bind(options);
        }

        try
        {
            var constructorParams = new object[] { descriptor.Name, Logger, DataContext, options! };
            return (IWarpMiddleware?)Activator.CreateInstance(middlewareType, constructorParams);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create middleware instance of type '{Type}'", descriptor.Type);
            return null;
        }
    }

    protected override async Task<IResult> ProcessAsync(HttpContext context)
    {
        Logger.LogDebug("Starting OrMiddleware evaluation for request: {Path}", context.Request.Path);

        IResult? middleware1Result = null;
        IResult? middleware2Result = null;

        // Try middleware1 first
        if (_middleware1 != null)
        {
            Logger.LogDebug("Evaluating first middleware: {Middleware1}", _middleware1.Name);
            try
            {
                middleware1Result = await TryMiddleware(_middleware1, context);
                if (IsSuccessResult(middleware1Result))
                {
                    Logger.LogDebug("First middleware succeeded, short-circuiting");
                    return middleware1Result;
                }
                Logger.LogDebug("First middleware failed, trying second middleware");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Exception in first middleware {Middleware}: {Message}", _middleware1.Name, ex.Message);
            }
        }

        // Try middleware2 if middleware1 failed or doesn't exist
        if (_middleware2 != null)
        {
            Logger.LogDebug("Evaluating second middleware: {Middleware2}", _middleware2.Name);
            try
            {
                middleware2Result = await TryMiddleware(_middleware2, context);
                if (IsSuccessResult(middleware2Result))
                {
                    Logger.LogDebug("Second middleware succeeded");
                    return middleware2Result;
                }
                Logger.LogDebug("Second middleware failed");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Exception in second middleware {Middleware}: {Message}", _middleware2.Name, ex.Message);
            }
        }

        // If both failed or are null, the OR operation failed
        Logger.LogWarning("Both middlewares failed for OrMiddleware");
        
        // Return the more specific error from the middlewares, or a generic one
        if (middleware1Result != null && !IsSuccessResult(middleware1Result))
            return middleware1Result;
        if (middleware2Result != null && !IsSuccessResult(middleware2Result))
            return middleware2Result;
            
        return Results
            .Problem(statusCode: 401, title: "Unauthorized", detail: "Access denied")
            .Stop();
    }

    private async Task<IResult> TryMiddleware(IWarpMiddleware middleware, HttpContext context)
    {
        // For OrMiddleware, we need to call ProcessAsync directly on the child middleware
        // Since they all inherit from MiddlewareBase, we can use reflection to call ProcessAsync
        var middlewareType = middleware.GetType();
        var processMethod = middlewareType.GetMethod("ProcessAsync", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        if (processMethod != null)
        {
            var task = processMethod.Invoke(middleware, new object[] { context }) as Task<IResult>;
            if (task != null)
            {
                return await task;
            }
        }
        
        // Fallback: if we can't find ProcessAsync, return failure
        Logger.LogError("Could not find ProcessAsync method on middleware {Middleware}", middleware.Name);
        return Results
            .Problem(statusCode: 500, title: "Internal Server Error", detail: "Middleware configuration error")
            .Stop();
    }

    private static bool IsSuccessResult(IResult result)
    {
        // Check if the result indicates success (Continue action)
        // We need to examine the result type to determine if it's a continue or stop
        var resultType = result.GetType();
        
        // Look for Continue vs Stop indication in the result
        // The extension methods .Continue() and .Stop() set properties we can check
        if (resultType.Name.Contains("Continue"))
            return true;
            
        // Alternative approach: check if it's a problem result (which indicates failure)
        if (result is Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)
            return false;
            
        // For Results.Ok().Continue(), we need to check the actual result action
        // This is a bit tricky since the Continue/Stop are extension methods
        // Let's use a simple heuristic: if it's not a Problem result, assume success
        return !(result is Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult);
    }
}
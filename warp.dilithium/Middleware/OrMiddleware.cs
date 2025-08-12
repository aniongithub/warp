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
    private readonly MiddlewareBase<MiddlewareOptions>? _middleware1;
    private readonly MiddlewareBase<MiddlewareOptions>? _middleware2;

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

    private MiddlewareBase<MiddlewareOptions>? CreateMiddleware(MiddlewareDescriptor? descriptor, IServiceProvider serviceProvider)
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
            return (MiddlewareBase<MiddlewareOptions>?)Activator.CreateInstance(middlewareType, constructorParams);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create middleware instance of type '{Type}'", descriptor.Type);
            return null;
        }
    }

    protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Check if we should apply this middleware to the current request
        if (!ShouldApplyToRequest(context))
        {
            Logger.LogDebug("OrMiddleware not applicable to request type: {Path}", context.Request.Path);
            await next(context);
            return;
        }

        Logger.LogDebug("Starting OrMiddleware evaluation for request: {Path}", context.Request.Path);

        bool middleware1Success = true;
        bool middleware2Success = true;

        // Try middleware1 first (short-circuit if successful)
        if (_middleware1 != null)
        {
            Logger.LogDebug("Evaluating first middleware: {Middleware1}", _middleware1.Name);
            middleware1Success = await TryMiddleware(_middleware1, context, next);
            if (middleware1Success)
            {
                Logger.LogDebug("First middleware succeeded, short-circuiting");
                return;
            }
            Logger.LogDebug("First middleware failed, trying second middleware");
        }

        // Try middleware2 if middleware1 failed or doesn't exist
        if (_middleware2 != null)
        {
            Logger.LogDebug("Evaluating second middleware: {Middleware2}", _middleware2.Name);
            middleware2Success = await TryMiddleware(_middleware2, context, next);
            if (middleware2Success)
            {
                Logger.LogDebug("Second middleware succeeded");
                return;
            }
            Logger.LogDebug("Second middleware failed");
        }

        // If both failed or are null, the OR operation failed
        if (_middleware1 == null)
            middleware1Success = false;
        if (_middleware2 == null)
            middleware2Success = false;

        if (!middleware1Success && !middleware2Success)
        {
            Logger.LogWarning("Both middlewares failed for OrMiddleware");
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Access denied");
            }
        }
        else
        {
            // This shouldn't happen due to short-circuiting, but just in case
            await next(context);
        }
    }

    private async Task<bool> TryMiddleware(MiddlewareBase<MiddlewareOptions> middleware, HttpContext context, RequestDelegate next)
    {
        try
        {
            // Create a fake "next" delegate that captures whether the middleware succeeded
            bool middlewareAllowedRequest = false;
            
            RequestDelegate fakeNext = (ctx) =>
            {
                middlewareAllowedRequest = true;
                return Task.CompletedTask;
            };

            // Store original response values to restore if middleware fails
            var originalStatusCode = context.Response.StatusCode;
            var originalHasStarted = context.Response.HasStarted;

            await middleware.InvokeWithTracingAsync(context, fakeNext);

            // If the middleware called next() and didn't set an error status, it succeeded
            if (middlewareAllowedRequest && context.Response.StatusCode < 400)
            {
                // Middleware succeeded, now call the real next
                await next(context);
                return true;
            }

            // Middleware failed, restore response state for next attempt
            if (!originalHasStarted && !context.Response.HasStarted)
            {
                context.Response.StatusCode = originalStatusCode;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Exception in middleware {Middleware}: {Message}", middleware.Name, ex.Message);
            return false;
        }
    }
}
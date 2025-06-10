// // Middleware with no configuration
// public interface IMiddleware
// {
// }

// // Middleware with strongly-typed configuration
// public interface IMiddleware<TConfig>: IMiddleware
// {
//     void Configure(TConfig options);
// }

using Warp.Core.Data;

namespace Warp.Middleware;

public class MiddlewareOptions
{
    public bool TracingEnabled { get; set; } = false;
    public string? TracingProvider { get; set; } = null;
    public string TracingProviderName { get; set; } = "DefaultTracingProvider";
}

public abstract class MiddlewareBase<TOptions>
    where TOptions : MiddlewareOptions
{
    protected TOptions Options { get; }
    protected ILogger Logger { get; }
    protected TracingProvider _tracingProvider = default!;
    protected IDataContext DataContext { get; }

    // => Options.TracingEnabled && Options.TracingProvider != null
    // ? (TracingProvider)Activator.CreateInstance(Type.GetType(Options.TracingProvider)!)
    // : null;

    protected MiddlewareBase(string name, ILogger logger, IDataContext context, TOptions options)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Options = options;
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        DataContext = context ?? throw new ArgumentNullException(nameof(context));
        if (Options.TracingEnabled && Options.TracingProvider != null)
        {
            var providerType = Type.GetType(Options.TracingProvider!);
            if (providerType == null)
                throw new InvalidOperationException($"Tracing provider type '{Options.TracingProvider}' could not be found.");
            _tracingProvider = (TracingProvider?)Activator.CreateInstance(providerType, Name)
                ?? throw new InvalidOperationException($"Tracing provider '{Options.TracingProvider}' could not be instantiated.");
            if (_tracingProvider == null)
                throw new InvalidOperationException($"Tracing provider '{Options.TracingProvider}' not found.");
        }
    }

    public async Task InvokeWithTracingAsync(HttpContext context, RequestDelegate next)
    {
        if (Options.TracingEnabled && _tracingProvider != null)
        {
            var traceParent = context.Request.Headers["traceparent"].FirstOrDefault() ?? string.Empty;
            using var span = _tracingProvider.Start(traceParent);
            await InvokeAsync(context, next);
            span.SetStatus(context.Response.StatusCode);
        }
        else
        {
            await InvokeAsync(context, next);
        }
    }

    protected virtual Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Default implementation just calls next
        return next(context);
    }

    public string Name { get; }
}

internal class MiddlewareDescriptor
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public IConfigurationSection? Options { get; set; } = default;
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Warp.Core.Data;
using Warp.Core.Helper;

namespace Warp.Core.Middleware;

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

public class MiddlewareDescriptor
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public IConfigurationSection? Options { get; set; } = default;
}

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
    public List<string> ApplyOn { get; set; } = new();
}

/// <summary>
/// Non-generic interface for middleware to allow polymorphic usage
/// </summary>
public interface IWarpMiddleware
{
    string Name { get; }
    Task InvokeWithTracingAsync(HttpContext context, RequestDelegate next);
}

public abstract class MiddlewareBase<TOptions> : IWarpMiddleware
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
        
        // Lazy initialization: set default ApplyOn if not configured
        if (Options.ApplyOn.Count == 0)
            Options.ApplyOn.AddRange(["Sync", "AsyncSubmit", "AsyncStatus", "AsyncResult", "AsyncCancel"]);
        
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

    protected bool ShouldApplyToRequest(HttpContext context)
    {
        var operationType = GetOperationType(context.Request.Path, context.Request.Method);
        return Options.ApplyOn.Contains(operationType, StringComparer.OrdinalIgnoreCase);
    }

    protected static string GetOperationType(string path, string method)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return "Sync";

        // Find operation keywords in the path
        var submitIndex = Array.FindIndex(segments, s => s.Equals("submit", StringComparison.OrdinalIgnoreCase));
        var statusIndex = Array.FindIndex(segments, s => s.Equals("status", StringComparison.OrdinalIgnoreCase));
        var resultIndex = Array.FindIndex(segments, s => s.Equals("result", StringComparison.OrdinalIgnoreCase));
        var cancelIndex = Array.FindIndex(segments, s => s.Equals("cancel", StringComparison.OrdinalIgnoreCase));

        // Check submit operation (should be last segment, POST method)
        if (submitIndex != -1 && submitIndex == segments.Length - 1 && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            return "AsyncSubmit";
        }

        // Check status operation (should be second-to-last, with jobId as last segment, GET method)
        if (statusIndex != -1 && statusIndex == segments.Length - 2 && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return "AsyncStatus";
        }

        // Check result operation (should be second-to-last, with jobId as last segment, GET method)
        if (resultIndex != -1 && resultIndex == segments.Length - 2 && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return "AsyncResult";
        }

        // Check cancel operation (should be second-to-last, with jobId as last segment, DELETE method)
        if (cancelIndex != -1 && cancelIndex == segments.Length - 2 && method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            return "AsyncCancel";
        }
        
        return "Sync";
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

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Warp.Core.Data;
using System.Diagnostics;

namespace Warp.Core.Middleware;

public class MiddlewareOptions
{
    public List<string> ApplyOn { get; set; } = new();
}

/// <summary>
/// Non-generic interface for middleware to allow polymorphic usage
/// </summary>
public interface IWarpMiddleware
{
    string Name { get; }
    Task<IResult> InvokeWithTracingAsync(HttpContext context, RequestDelegate next);
}

public abstract class MiddlewareBase<TOptions> : IWarpMiddleware
    where TOptions : MiddlewareOptions
{
    protected TOptions Options { get; }
    protected ILogger Logger { get; }
    protected IDataContext DataContext { get; }
    private static readonly ActivitySource ActivitySource = new("Warp");

    protected MiddlewareBase(string name, ILogger logger, IDataContext context, TOptions options)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Options = options;
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        DataContext = context ?? throw new ArgumentNullException(nameof(context));
        
        // Lazy initialization: set default ApplyOn if not configured
        if (Options.ApplyOn.Count == 0)
            Options.ApplyOn.AddRange(["Sync", "AsyncSubmit", "AsyncStatus", "AsyncResult", "AsyncCancel"]);
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

        // Check submit operation (should be last segment, POST or GET method)
        if (submitIndex != -1 && submitIndex == segments.Length - 1 && 
            (method.Equals("POST", StringComparison.OrdinalIgnoreCase) || method.Equals("GET", StringComparison.OrdinalIgnoreCase)))
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

    public async Task<IResult> InvokeWithTracingAsync(HttpContext context, RequestDelegate next)
    {
        if (!ShouldApplyToRequest(context))
        {
            await next(context);
            return Results.Ok().Continue(); // Continue pipeline since we didn't apply
        }

        using var activity = ActivitySource.StartActivity($"Middleware.{Name}");
        activity?.SetTag("middleware.name", Name);

        IResult result = await ProcessAsync(context);
        
        // Always execute result (it will handle Stop/Continue internally)
        await result.ExecuteAsync(context);
        
        // Only call next if the result indicates Continue
        if (result is Result warpResult && warpResult.Action == PipelineAction.Continue)
        {
            await next(context);
        }
        
        activity?.SetTag("response.status_code", context.Response.StatusCode);
        
        // Return the result so the caller can inspect Continue/Stop decision
        return result;
    }

    /// <summary>
    /// Main processing method that middleware implementations should override.
    /// Returns an IResult to control pipeline flow.
    /// Use .Continue() or .Stop() extension methods for explicit pipeline control.
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <returns>IResult to execute and control pipeline flow</returns>
    protected virtual Task<IResult> ProcessAsync(HttpContext context)
    {
        // Default implementation continues the pipeline
        return Task.FromResult<IResult>(Results.Empty.Continue());
    }


    public string Name { get; }
}

public class MiddlewareDescriptor
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public IConfigurationSection? Options { get; set; } = default;
}

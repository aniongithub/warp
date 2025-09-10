using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Warp.Core.Middleware;

/// <summary>
/// Indicates how the middleware pipeline should proceed after executing a result
/// </summary>
public enum PipelineAction
{
    /// <summary>
    /// Stop the pipeline after executing the result (default behavior)
    /// </summary>
    Stop,

    /// <summary>
    /// Continue the pipeline after executing the result
    /// </summary>
    Continue
}

/// <summary>
/// Wrapper that adds pipeline control to any IResult
/// </summary>
public class Result : IResult
{
    private readonly IResult _innerResult;
    private static readonly ILogger _logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<Result>();

    public PipelineAction Action { get; }

    public Result(IResult innerResult, PipelineAction action)
    {
        _innerResult = innerResult ?? throw new ArgumentNullException(nameof(innerResult));
        Action = action;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        // Only execute the inner result if we're stopping the pipeline
        if (Action == PipelineAction.Stop)
        {
            _logger.LogDebug("Executing result and stopping pipeline for {Path}", httpContext.Request.Path);
            await _innerResult.ExecuteAsync(httpContext);
        }
        else
        {
            _logger.LogDebug("Continuing pipeline without executing result for {Path}", httpContext.Request.Path);
        }
        // If Action is Continue, we don't execute the result - let the pipeline continue
    }

    public IResult InnerResult => _innerResult;
}

/// <summary>
/// Extension methods to add fluent pipeline control to IResult
/// </summary>
public static class MiddlewareResultExtensions
{
    /// <summary>
    /// Indicates that this result should continue the middleware pipeline after execution.
    /// </summary>
    public static Result Continue(this IResult result) => new(result, PipelineAction.Continue);

    /// <summary>
    /// Indicates that this result should stop the middleware pipeline after execution.
    /// This is the default behavior for unwrapped IResult.
    /// </summary>
    public static Result Stop(this IResult result) => new(result, PipelineAction.Stop);
}
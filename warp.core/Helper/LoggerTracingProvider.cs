using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Warp.Core.Helper;

/// <summary>
/// Lightweight tracing provider that uses Microsoft.Extensions.Logging with scopes for indented output
/// </summary>
public class LoggerTracingProvider : TracingProvider
{
    private readonly ILogger _logger;
    internal static readonly ActivitySource ActivitySource = new("Warp.Logger");

    public LoggerTracingProvider(string name, ILogger? logger = null) : base(name)
    {
        _logger = logger ?? LoggerFactory.Create(builder => 
            builder.AddConsole(options => 
            {
                options.FormatterName = "simple";
            }).AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
            })).CreateLogger(name);
    }

    protected override TraceSpan CreateSpan(string traceParent)
    {
        return new LoggerTraceSpan(_logger, Name, traceParent);
    }

    protected override void OnDispose()
    {
        // Nothing to dispose for logger-based tracing
    }
}

/// <summary>
/// Trace span that uses ILogger scopes for hierarchical logging with W3C trace context
/// </summary>
public class LoggerTraceSpan : TraceSpan
{
    private readonly ILogger _logger;
    private readonly IDisposable? _scope;
    private readonly string _operationName;
    private readonly DateTime _startTime;
    private readonly Dictionary<string, object> _tags;
    private readonly Activity? _activity;

    public LoggerTraceSpan(ILogger logger, string operationName, string traceParent)
    {
        _logger = logger;
        _operationName = operationName;
        _startTime = DateTime.UtcNow;
        _tags = new Dictionary<string, object>();

        // Parse traceParent and create Activity for proper W3C context
        ActivityContext parentContext = default;
        if (!string.IsNullOrEmpty(traceParent))
        {
            ActivityContext.TryParse(traceParent, null, out parentContext);
        }
        
        // Create activity using the parsed parent context or current activity
        _activity = LoggerTracingProvider.ActivitySource.StartActivity(operationName, ActivityKind.Internal, 
            parentContext != default ? parentContext : Activity.Current?.Context ?? default);

        // Create a logging scope for this span with W3C trace context
        var scopeData = new Dictionary<string, object>
        {
            ["Operation"] = operationName,
            ["StartTime"] = _startTime.ToString("HH:mm:ss.fff")
        };

        if (_activity != null)
        {
            scopeData["TraceId"] = _activity.TraceId.ToString();
            scopeData["SpanId"] = _activity.SpanId.ToString();
            if (_activity.ParentSpanId != default)
            {
                scopeData["ParentSpanId"] = _activity.ParentSpanId.ToString();
            }
        }

        _scope = _logger.BeginScope(scopeData);
        
        _logger.LogInformation("→ Starting {Operation} [TraceId: {TraceId}, SpanId: {SpanId}]", 
            operationName, _activity?.TraceId.ToString() ?? "none", _activity?.SpanId.ToString() ?? "none");
    }

    protected override void OnSetTag(string key, string value)
    {
        _tags[key] = value;
        _activity?.SetTag(key, value);
        _logger.LogDebug("Tag: {Key} = {Value}", key, value);
    }

    protected override void OnSetException(Exception ex)
    {
        _activity?.AddException(ex);
        _logger.LogError(ex, "Exception in {Operation}: {Message}", _operationName, ex.Message);
    }

    protected override void OnSetStatus(int status)
    {
        var level = status >= 400 ? LogLevel.Warning : LogLevel.Information;
        _activity?.SetTag("http.status_code", status);
        _logger.Log(level, "Status: {StatusCode}", status);
    }

    protected override void OnStop()
    {
        var duration = DateTime.UtcNow - _startTime;
        _logger.LogInformation("← Completed {Operation} in {Duration:F2}ms [TraceId: {TraceId}, SpanId: {SpanId}]", 
            _operationName, duration.TotalMilliseconds, 
            _activity?.TraceId.ToString() ?? "none", _activity?.SpanId.ToString() ?? "none");
        
        _activity?.Stop();
    }

    protected override void OnDispose()
    {
        _activity?.Dispose();
        _scope?.Dispose();
    }
}

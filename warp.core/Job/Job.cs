using Warp.Core.Data;

namespace Warp.Core.Job;

public class Job : IJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public IUser? User { get; set; } = null;
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; } = null;
    public DateTime? EndedAt { get; set; } = null;
    public string? Error { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Warp internal routing data
    public string OriginalPath { get; set; } = string.Empty;
    public string ClusterId { get; set; } = string.Empty;
    public string TargetDestination { get; set; } = string.Empty;
    public Dictionary<string, object?> Parameters { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = new();

    // Complete input mapping configuration for HTTP request reconstruction
    public Dictionary<string, ParameterMapping> ParameterMappings { get; set; } = new();

    // Distributed tracing context
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }

    // Actual API input/output data
    public string? Input { get; set; } = string.Empty;
    public string? Output { get; set; } = string.Empty;
}

/// <summary>
/// Stores the mapping configuration for a parameter to reconstruct HTTP requests
/// </summary>
public class ParameterMapping
{
    public InputSource From { get; set; } = new();
    public bool Required { get; set; } = false;
    public string? Default { get; set; }
    public TransformConfig? Transform { get; set; }
}

/// <summary>
/// Defines where a parameter comes from in the HTTP request
/// </summary>
public class InputSource
{
    public string? Header { get; set; }
    public string? Query { get; set; }
    public string? Body { get; set; }
}

/// <summary>
/// Configuration for parameter transforms
/// </summary>
public class TransformConfig
{
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, object?> Options { get; set; } = new();
}

/// <summary>
/// Distributed tracing context for jobs
/// </summary>
public class TracingContext
{
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }
}

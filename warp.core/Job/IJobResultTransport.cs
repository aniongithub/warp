namespace Warp.Core.Job;

/// <summary>
/// Interface for asymmetric job result delivery operations
/// </summary>
public interface IJobResultTransport
{
    /// <summary>
    /// Deliver job completion result
    /// </summary>
    Task DeliverJobResultAsync(IJob job, string result);
    
    /// <summary>
    /// Deliver job error
    /// </summary>
    Task DeliverJobErrorAsync(IJob job, string error);
    
    /// <summary>
    /// Deliver job status update
    /// </summary>
    Task DeliverJobStatusAsync(IJob job, JobStatus status);
    
    /// <summary>
    /// Get job result for API queries (optional - some transports may not support this)
    /// </summary>
    Task<JobResult?> GetJobResultAsync(string jobId, string? userId = null);
}

/// <summary>
/// Base configuration for job result transport implementations
/// </summary>
public abstract class JobResultTransportOptions
{
    public string Type { get; set; } = string.Empty;
}

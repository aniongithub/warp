namespace Warp.Core.Job;

/// <summary>
/// Interface for symmetric job transport operations (job submission and pickup)
/// </summary>
public interface IJobTransport
{
    /// <summary>
    /// Enqueue a job for processing
    /// </summary>
    Task<string> EnqueueJobAsync(IJob job);
    
    /// <summary>
    /// Dequeue the next available job for processing
    /// </summary>
    Task<TJob> DequeueJobAsync<TJob>() where TJob : IJob;
    
    /// <summary>
    /// Update job status without affecting result delivery
    /// </summary>
    Task UpdateJobStatusAsync(string jobId, JobStatus status, string? error = null);
    
    /// <summary>
    /// Get current job status
    /// </summary>
    Task<JobStatus> GetJobStatusAsync(string jobId, string? userId = null);
    
    /// <summary>
    /// Lookup a job by ID and status
    /// </summary>
    Task<TJob?> LookupJobAsync<TJob>(string jobId, JobStatus status, string? userId = null) where TJob : IJob;
    
    /// <summary>
    /// Cancel a job
    /// </summary>
    Task CancelJobAsync(string jobId, string? userId = null);
}

/// <summary>
/// Base configuration for job transport implementations
/// </summary>
public abstract class JobTransportOptions
{
    public string Type { get; set; } = string.Empty;
}

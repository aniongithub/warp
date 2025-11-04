namespace Warp.Core.Job;

public class DequeueResult<T> where T : class, IJob
{
    public T? Job { get; }
    public bool HasJob => Job != null;
    
    private DequeueResult(T? job)
    {
        Job = job;
    }
    
    public static DequeueResult<T> Success(T job) => new(job);
    public static DequeueResult<T> NoJob() => new(null);
}

public interface IJobContext
{
    void Initialize(string connectionString, string channel);
    IJob CreateJob();
    Task EnqueueJobAsync<T>(T job) where T : class, IJob;
    Task<DequeueResult<T>> DequeueJobAsync<T>() where T : class, IJob;
    Task<T> LookupJobAsync<T>(string id, JobStatus status, string userId) where T : class, IJob;
    Task<JobStatus> GetJobStatusAsync(string id, string userId);
    Task<IAsyncEnumerable<T>> ListJobs<T>(string userId, JobStatus status, int batchSize) where T : class, IJob;
    Task UpdateJobAsync<T>(T job, JobStatus newStatus, string? error = null, string? output = null) where T : class, IJob;
    Task<bool> UpdateJobStatusAsync(string jobId, JobStatus fromStatus, JobStatus toStatus, string userId, string? error = null, string? output = null);
    
    // Serialization methods for job persistence
    string SerializeJob<T>(T job) where T : class, IJob;
    T DeserializeJob<T>(string jobData) where T : class, IJob;
}

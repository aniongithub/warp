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

    // Reliable-queue operations for at-least-once processing.
    // Requeue an in-flight (Running) job back to the Queued state so it can be retried.
    Task RequeueJobAsync<T>(T job) where T : class, IJob;
    // Recover jobs that were left in the in-flight processing list by a crashed worker.
    // Jobs under the attempt cap are requeued; those over it are dead-lettered (Failed).
    // Returns the number of jobs recovered.
    Task<int> RecoverProcessingJobsAsync<T>(int maxAttempts) where T : class, IJob;

    // Serialization methods for job persistence
    string SerializeJob<T>(T job) where T : class, IJob;
    T DeserializeJob<T>(string jobData) where T : class, IJob;
}

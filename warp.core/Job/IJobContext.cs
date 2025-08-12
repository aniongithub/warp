namespace Warp.Core.Job;

public interface IJobContext
{
    IJob CreateJob();
    Task EnqueueJobAsync<T>(T job) where T : class, IJob;
    Task<T> DequeueJobAsync<T>() where T : class, IJob;
    Task<T> LookupJobAsync<T>(string id, JobStatus status, string userId) where T : class, IJob;
    Task<JobStatus> GetJobStatusAsync(string id, string userId);
    Task<IAsyncEnumerable<T>> ListJobs<T>(string userId, JobStatus status, int batchSize) where T : class, IJob;
    
    // Serialization methods for job persistence
    string SerializeJob<T>(T job) where T : class, IJob;
    T DeserializeJob<T>(string jobData) where T : class, IJob;
}

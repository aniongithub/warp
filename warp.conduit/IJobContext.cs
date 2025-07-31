using System.Data.Common;
using System.Linq.Expressions;
using Google.Rpc;

namespace Warp.Core.Data;

public interface IJobContext
{
    IJob CreateJob();
    Task EnqueueJobAsync<T>(T job) where T : IJob;
    Task<T> DequeueJobAsync<T>() where T : IJob;
    Task<T?> LookupJobAsync<T>(string id, JobStatus status, string userId) where T : IJob;
    Task<JobStatus> GetJobStatusAsync(string id, string userId);
    Task<IAsyncEnumerable<T>> ListJobs<T>(string userId, JobStatus status, int batchSize) where T : IJob;
}
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Warp.Core.Data;
using Warp.Core.Job;
using Warp.Core.Job.Contexts;

namespace Warp.Dilithium.Middleware;

public class RedisAsyncApiHandlerOptions : AsyncApiHandlerOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public int DatabaseIndex { get; set; } = 0;
}

public sealed class RedisAsyncApiHandler : AsyncApiHandler<RedisAsyncApiHandlerOptions>
{
    private readonly RedisJobContext _jobContext;

    public RedisAsyncApiHandler(string name, ILogger logger, IDataContext context, RedisAsyncApiHandlerOptions options) 
        : base(name, logger, context, options)
    {
        _jobContext = new RedisJobContext(options.Channel, options.ConnectionString, options.DatabaseIndex);
    }

    protected override async Task<string> CreateAndEnqueueJobAsync(IUser user, Dictionary<string, object?> extractedInputs, Dictionary<string, ParameterMapping> parameterMappings, JobRoutingInfo routingInfo, TracingContext tracingContext)
    {
        var job = new Job
        {
            User = user,
            QueuedAt = DateTime.UtcNow,
            Status = JobStatus.Queued,
            // Set routing data directly as fields
            OriginalPath = routingInfo.OriginalPath,
            ClusterId = routingInfo.ClusterId,
            TargetDestination = routingInfo.TargetDestination,
            Parameters = extractedInputs,
            Headers = routingInfo.Headers,
            ParameterMappings = parameterMappings,
            // Set tracing context
            TraceParent = tracingContext.TraceParent,
            TraceState = tracingContext.TraceState
        };

        await _jobContext.EnqueueJobAsync(job);
        return job.Id;
    }

    protected override async Task<JobStatus> GetJobStatusAsync(string jobId, string userId)
    {
        return await _jobContext.GetJobStatusAsync(jobId, userId);
    }

    protected override async Task<JobResult> GetJobResultAsync(string jobId, string userId)
    {
        // Try to find the job in any status
        Job? job = null;
        foreach (JobStatus status in Enum.GetValues(typeof(JobStatus)))
        {
            job = await _jobContext.LookupJobAsync<Job>(jobId, status, userId);
            if (job != null) break;
        }

        if (job == null)
        {
            throw new KeyNotFoundException($"Job '{jobId}' not found");
        }

        return new JobResult
        {
            JobId = job.Id,
            Status = job.Status,
            QueuedAt = job.QueuedAt,
            StartedAt = job.StartedAt,
            EndedAt = job.EndedAt,
            Error = job.Error,
            Output = job.Output
        };
    }

    protected override async Task CancelJobAsync(string jobId, string userId)
    {
        // Try to find and cancel the job if it's still queued or running
        var currentStatus = await _jobContext.GetJobStatusAsync(jobId, userId);
        
        if (currentStatus == JobStatus.Completed || currentStatus == JobStatus.Failed || currentStatus == JobStatus.Canceled)
        {
            throw new InvalidOperationException($"Cannot cancel job '{jobId}' - it is already {currentStatus}");
        }

        var job = await _jobContext.LookupJobAsync<Job>(jobId, currentStatus, userId);
        if (job == null)
        {
            throw new KeyNotFoundException($"Job '{jobId}' not found");
        }

        // Update job to cancelled status
        job.Status = JobStatus.Canceled;
        job.EndedAt = DateTime.UtcNow;

        await _jobContext.EnqueueJobAsync(job);
    }
}

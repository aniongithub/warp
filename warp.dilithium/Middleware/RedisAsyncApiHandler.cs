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
    // ConnectionString is inherited from AsyncApiHandlerOptions
    // Channel is inherited from AsyncApiHandlerOptions
}

public sealed class RedisAsyncApiHandler : AsyncApiHandler<RedisAsyncApiHandlerOptions, RedisJobContext>
{
    public RedisAsyncApiHandler(string name, ILogger logger, IDataContext context, RedisAsyncApiHandlerOptions options) 
        : base(name, logger, context, options)
    {
    }

    protected override async Task<string> CreateAndEnqueueJobAsync(IUser user, Dictionary<string, object?> extractedInputs, Dictionary<string, ParameterMapping> parameterMappings, JobRoutingInfo routingInfo, TracingContext tracingContext)
    {
        var job = new Job
        {
            User = user,
            QueuedAt = DateTime.UtcNow,
            Status = JobStatus.Queued,
            OriginalPath = routingInfo.OriginalPath,
            ClusterId = routingInfo.ClusterId,
            TargetDestination = routingInfo.TargetDestination,
            Parameters = extractedInputs,
            Headers = routingInfo.Headers,
            ParameterMappings = parameterMappings,
            TraceParent = tracingContext.TraceParent,
            TraceState = tracingContext.TraceState
        };

        await JobContext.EnqueueJobAsync(job);
        return job.Id;
    }
}

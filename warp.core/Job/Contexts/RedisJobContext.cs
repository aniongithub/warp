using StackExchange.Redis;
using Warp.Core.Data;

namespace Warp.Core.Job.Contexts;

public class RedisJobContext : JobContextBase
{
    private IDatabase? _db;
    private string _channel = string.Empty;

    public RedisJobContext()
    {
        // Parameterless constructor for generic instantiation
    }

    public override void Initialize(string connectionString, string channel)
    {
        // Parse connection string for Redis
        // Format: "localhost:6379" or "localhost:6379,database=1" etc.
        var configOptions = ConfigurationOptions.Parse(connectionString);
        
        _channel = channel;
        
        var multiplexer = ConnectionMultiplexer.Connect(configOptions);
        _db = multiplexer.GetDatabase();
    }

    private void EnsureInitialized()
    {
        if (_db == null)
        {
            throw new InvalidOperationException("RedisJobContext has not been initialized. Call Initialize() first.");
        }
    }

    private string JobKey(string id, JobStatus status, string userId) => $"channel:{_channel}:job:{id}@{status}:{userId}";
    private string QueueKey(JobStatus status) => $"channel:{_channel}:queue:{status}";
    // Reliable-queue "processing" list: holds the full job keys currently being dispatched by a worker.
    // An entry lingering here after a crash marks an in-flight job that must be recovered.
    private string ProcessingKey() => $"channel:{_channel}:processing";

    public override IJob CreateJob() => new Job();

    public override async Task EnqueueJobAsync<T>(T job)
    {
        EnsureInitialized();
        // TODO: Replace with proper logging
        Console.WriteLine($"Enqueuing job: {job.Id}");
        if (string.IsNullOrEmpty(job.User?.Id)) throw new ArgumentException("User.Id required");
        var key = JobKey(job.Id, job.Status, job.User.Id);
        var queueKey = QueueKey(job.Status);

        try
        {
            Console.WriteLine($"Storing job in Redis with key: {key}");
            await _db!.StringSetAsync(key, SerializeJob(job));

            // The queue carries the FULL job key (not just the id) so consumers can fetch
            // the job data with a direct O(1) GET instead of an O(N) wildcard KEYS scan.
            Console.WriteLine($"Pushing job key to queue: {queueKey}");
            await _db.ListRightPushAsync(queueKey, key);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error enqueuing job: {ex.Message}");
            throw;
        }
    }

    public override async Task<DequeueResult<T>> DequeueJobAsync<T>()
    {
        EnsureInitialized();

        var processingKey = ProcessingKey();

        // Reliably claim the next queued job: atomically pop the head of the Queued list and
        // push it onto the processing list (Redis LMOVE). If the worker crashes after this point
        // the entry survives on the processing list and is recovered on the next startup, so an
        // in-flight job is never silently lost (at-least-once).
        var moved = await _db!.ListMoveAsync(QueueKey(JobStatus.Queued), processingKey, ListSide.Left, ListSide.Right);

        if (moved.IsNullOrEmpty)
        {
            return DequeueResult<T>.NoJob(); // No job available - this is normal, not an exception
        }

        // The queue now carries the FULL job key, so fetch the job data directly (no wildcard scan).
        var queuedKey = moved.ToString();
        var jobData = await _db.StringGetAsync(queuedKey);

        if (jobData.IsNullOrEmpty)
        {
            // Dangling queue entry with no backing data (e.g. already consumed elsewhere). Drop it.
            await _db.ListRemoveAsync(processingKey, queuedKey);
            return DequeueResult<T>.NoJob();
        }

        // Deserialize the job data
        var jobDataString = jobData.ToString();
        T job;
        try
        {
            job = DeserializeJob<T>(jobDataString)!;
        }
        catch (Exception ex)
        {
            // Poison message: remove it from the processing list so it doesn't wedge the worker.
            await _db.ListRemoveAsync(processingKey, queuedKey);
            throw new InvalidOperationException("Failed to deserialize job.", ex);
        }

        if (string.IsNullOrEmpty(job.User?.Id))
        {
            await _db.ListRemoveAsync(processingKey, queuedKey);
            throw new ArgumentException("User.Id required");
        }

        var userId = job.User.Id;
        var runningKey = JobKey(job.Id, JobStatus.Running, userId);

        job.Status = JobStatus.Running;
        job.StartedAt = DateTime.UtcNow;

        // Atomically move the job from Queued to Running and repoint the processing marker at the
        // new Running key so the crash-recovery net keeps tracking the in-flight job.
        var transaction = _db.CreateTransaction();
        transaction.AddCondition(Condition.KeyExists(queuedKey));
        _ = transaction.StringSetAsync(runningKey, SerializeJob(job));
        _ = transaction.ListRightPushAsync(QueueKey(JobStatus.Running), runningKey);
        _ = transaction.KeyDeleteAsync(queuedKey);
        _ = transaction.ListRemoveAsync(processingKey, queuedKey);
        _ = transaction.ListRightPushAsync(processingKey, runningKey);

        var committed = await transaction.ExecuteAsync();
        if (!committed)
        {
            // Another actor transitioned the job out of Queued first; clean up our marker and skip.
            await _db.ListRemoveAsync(processingKey, queuedKey);
            return DequeueResult<T>.NoJob();
        }

        return DequeueResult<T>.Success(job);
    }

    public override async Task<T> LookupJobAsync<T>(string id, JobStatus status, string userId)
    {
        EnsureInitialized();
        
        // If userId is "*", use wildcard pattern search
        if (userId == "*")
        {
            var server = _db!.Multiplexer.GetServer(_db.Multiplexer.GetEndPoints().First());
            var pattern = JobKey(id, status, "*");
            var keys = server.Keys(pattern: pattern).ToList();
            
            if (keys.Count == 0)
            {
                throw new KeyNotFoundException($"Job {id} not found");
            }
            
            var jobKey = keys.First();
            var jobData = await _db.StringGetAsync(jobKey);
            
            if (!jobData.HasValue || string.IsNullOrEmpty(jobData))
            {
                throw new KeyNotFoundException($"Job {id} not found");
            }
            
            return DeserializeJob<T>(jobData.ToString());
        }
        else
        {
            // Standard exact lookup
            var key = JobKey(id, status, userId);
            var jobData = await _db!.StringGetAsync(key);
            var jobStr = jobData.ToString();
            
            if (!jobData.HasValue || string.IsNullOrEmpty(jobStr))
                throw new KeyNotFoundException($"Job {id} not found");
                
            return DeserializeJob<T>(jobStr);
        }
    }

    public override async Task<JobStatus> GetJobStatusAsync(string id, string userId)
    {
        EnsureInitialized();
        foreach (JobStatus status in Enum.GetValues(typeof(JobStatus)))
        {
            var key = JobKey(id, status, userId);
            if (await _db!.KeyExistsAsync(key))
                return status;
        }
        throw new KeyNotFoundException("Job not found");
    }

    public override async Task<IAsyncEnumerable<T>> ListJobs<T>(string userId, JobStatus status, int batchSize)
    {
        EnsureInitialized();
        var pattern = JobKey("*", status, userId);
        var server = _db!.Multiplexer.GetServer(_db.Multiplexer.GetEndPoints().First());
        var keys = server.Keys(pattern: pattern, pageSize: batchSize).ToList();
        var jobs = new List<T>();
        foreach (var key in keys)
        {
            var jobData = await _db.StringGetAsync(key);
            var jobStr = jobData.ToString();
            if (jobData.HasValue && !string.IsNullOrEmpty(jobStr))
            {
                var job = DeserializeJob<T>(jobStr);
                jobs.Add(job);
            }
        }
        return jobs.ToAsyncEnumerable();
    }

    public override async Task UpdateJobAsync<T>(T job, JobStatus newStatus, string? error = null, string? output = null)
    {
        EnsureInitialized();
        if (string.IsNullOrEmpty(job.User?.Id)) throw new ArgumentException("User.Id required");
        
        var oldKey = JobKey(job.Id, job.Status, job.User.Id);
        var newKey = JobKey(job.Id, newStatus, job.User.Id);
        
        // Update job properties
        var oldStatus = job.Status;
        job.Status = newStatus;
        job.EndedAt = DateTime.UtcNow;
        
        if (!string.IsNullOrEmpty(error))
            job.Error = error;
        
        if (!string.IsNullOrEmpty(output))
            job.Output = output;
        
        // Move job to new status (queues carry the full job key, not the bare id)
        await _db!.StringSetAsync(newKey, SerializeJob(job));
        await _db.ListRightPushAsync(QueueKey(newStatus), newKey);

        // Remove from old status
        await _db.KeyDeleteAsync(oldKey);
        await _db.ListRemoveAsync(QueueKey(oldStatus), oldKey);

        // Clear the in-flight processing marker (no-op unless the job was Running).
        await _db.ListRemoveAsync(ProcessingKey(), oldKey);
    }

    public override async Task<bool> UpdateJobStatusAsync(string jobId, JobStatus fromStatus, JobStatus toStatus, string userId, string? error = null, string? output = null)
    {
        EnsureInitialized();
        
        // Find the job with the from status (using wildcard if userId is "*")
        Job? job = null;
        try
        {
            job = await LookupJobAsync<Job>(jobId, fromStatus, userId);
        }
        catch (KeyNotFoundException)
        {
            // Job not found in the expected status
            return false;
        }
        
        if (job == null)
            return false;
            
        // Get the actual user ID for key construction
        var actualUserId = job.User?.Id ?? "";
        if (string.IsNullOrEmpty(actualUserId))
            return false;
            
        var fromKey = JobKey(jobId, fromStatus, actualUserId);
        var toKey = JobKey(jobId, toStatus, actualUserId);
        
        // Use Redis transaction for atomicity
        var transaction = _db!.CreateTransaction();
        
        // Only proceed if the from key still exists (ensures atomicity)
        transaction.AddCondition(Condition.KeyExists(fromKey));
        
        // Update job properties
        job.Status = toStatus;
        job.EndedAt = DateTime.UtcNow;
        
        if (!string.IsNullOrEmpty(error))
            job.Error = error;
        
        if (!string.IsNullOrEmpty(output))
            job.Output = output;
        
        // Queue the operations (queues carry the full job key, not the bare id)
        _ = transaction.StringSetAsync(toKey, SerializeJob(job));
        _ = transaction.ListRightPushAsync(QueueKey(toStatus), toKey);
        _ = transaction.KeyDeleteAsync(fromKey);
        _ = transaction.ListRemoveAsync(QueueKey(fromStatus), fromKey);
        // Clear the in-flight processing marker (no-op unless the job was Running).
        _ = transaction.ListRemoveAsync(ProcessingKey(), fromKey);
        
        // Execute atomically
        var success = await transaction.ExecuteAsync();
        return success;
    }

    public override async Task RequeueJobAsync<T>(T job)
    {
        EnsureInitialized();
        if (string.IsNullOrEmpty(job.User?.Id)) throw new ArgumentException("User.Id required");

        var userId = job.User.Id;
        var fromKey = JobKey(job.Id, job.Status, userId);
        var queuedKey = JobKey(job.Id, JobStatus.Queued, userId);

        // Reset the job to Queued for another dispatch attempt. Attempts is expected to have been
        // incremented by the caller before requeueing so the attempt cap is honored.
        job.Status = JobStatus.Queued;
        job.StartedAt = null;
        job.EndedAt = null;

        // Atomically move the job back onto the Queued list and drop the in-flight marker.
        var transaction = _db!.CreateTransaction();
        _ = transaction.StringSetAsync(queuedKey, SerializeJob(job));
        _ = transaction.ListRightPushAsync(QueueKey(JobStatus.Queued), queuedKey);
        if (fromKey != queuedKey)
        {
            _ = transaction.KeyDeleteAsync(fromKey);
            _ = transaction.ListRemoveAsync(QueueKey(JobStatus.Running), fromKey);
        }
        _ = transaction.ListRemoveAsync(ProcessingKey(), fromKey);

        await transaction.ExecuteAsync();
    }

    public override async Task<int> RecoverProcessingJobsAsync<T>(int maxAttempts)
    {
        EnsureInitialized();

        var processingKey = ProcessingKey();
        var entries = await _db!.ListRangeAsync(processingKey);
        var recovered = 0;

        foreach (var entry in entries)
        {
            if (entry.IsNullOrEmpty)
            {
                await _db.ListRemoveAsync(processingKey, entry);
                continue;
            }

            var jobKey = entry.ToString();
            var jobData = await _db.StringGetAsync(jobKey);

            if (jobData.IsNullOrEmpty)
            {
                // No backing data; just drop the stale marker.
                await _db.ListRemoveAsync(processingKey, entry);
                continue;
            }

            T job;
            try
            {
                job = DeserializeJob<T>(jobData.ToString())!;
            }
            catch
            {
                // Poison message: remove marker and delete the unusable key.
                await _db.ListRemoveAsync(processingKey, entry);
                await _db.KeyDeleteAsync(jobKey);
                continue;
            }

            if (string.IsNullOrEmpty(job.User?.Id))
            {
                await _db.ListRemoveAsync(processingKey, entry);
                continue;
            }

            // The job was in-flight when a worker crashed. Count the interrupted attempt and either
            // requeue it (under the cap) or dead-letter it (Failed) so it can never be lost.
            job.Attempts += 1;

            if (job.Attempts < maxAttempts)
            {
                await RequeueJobAsync(job);
            }
            else
            {
                await UpdateJobAsync(job, JobStatus.Failed,
                    error: $"Dead-lettered after {job.Attempts} attempt(s): worker crashed while dispatching (recovered from processing list).");
            }

            recovered++;
        }

        return recovered;
    }
}

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
        
            Console.WriteLine($"Pushing job ID to queue: {queueKey}");
            await _db.ListRightPushAsync(queueKey, job.Id);
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
        // Pop job ID from the QUEUED queue (FIFO)
        var jobIdValue = await _db!.ListLeftPopAsync(QueueKey(JobStatus.Queued));
        
        if (!jobIdValue.HasValue || string.IsNullOrEmpty(jobIdValue))
        {
            return DequeueResult<T>.NoJob(); // No job available - this is normal, not an exception
        }
        
        var jobId = jobIdValue.ToString();
        
        // Find the actual job data using the job ID
        var server = _db.Multiplexer.GetServer(_db.Multiplexer.GetEndPoints().First());
        var pattern = JobKey(jobId, JobStatus.Queued, "*"); // Use wildcard for user ID
        var keys = server.Keys(pattern: pattern).ToList();
        
        if (keys.Count == 0)
        {
            throw new InvalidOperationException($"Job data not found for job ID: {jobId}");
        }
        
        var jobKey = keys.First();
        var jobData = await _db.StringGetAsync(jobKey);
        
        if (!jobData.HasValue || string.IsNullOrEmpty(jobData))
        {
            throw new InvalidOperationException($"Job data is empty for job ID: {jobId}");
        }
        
        // Deserialize the job data
        var jobDataString = jobData.ToString();
        try
        {
            var job = DeserializeJob<T>(jobDataString)!;
            // if (job == null) throw new InvalidOperationException("Failed to deserialize job.");
            
            // Move job to Running status
            if (string.IsNullOrEmpty(job.User?.Id)) throw new ArgumentException("User.Id required");
            
            var fromKey = JobKey(job.Id, JobStatus.Queued, job.User.Id);
            var toKey = JobKey(job.Id, JobStatus.Running, job.User.Id);
            
            job.Status = JobStatus.Running;
            job.StartedAt = DateTime.UtcNow;
            
            await _db.StringSetAsync(toKey, SerializeJob(job));
            await _db.ListRightPushAsync(QueueKey(JobStatus.Running), job.Id);
            await _db.KeyDeleteAsync(fromKey);
            
            return DequeueResult<T>.Success(job);

        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to deserialize job.", ex);
        }
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
        
        // Move job to new status
        await _db!.StringSetAsync(newKey, SerializeJob(job));
        await _db.ListRightPushAsync(QueueKey(newStatus), job.Id);
        
        // Remove from old status
        await _db.KeyDeleteAsync(oldKey);
        await _db.ListRemoveAsync(QueueKey(oldStatus), job.Id);
    }
}

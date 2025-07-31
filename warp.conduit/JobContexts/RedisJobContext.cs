using StackExchange.Redis;

namespace Warp.Core.Data.JobContexts;

public class RedisJobContext : IJobContext
{
    private readonly IDatabase _db;
    private readonly string _channel;

    public RedisJobContext(string channel, string connectionString, int dbIndex = 0)
    {
        var multiplexer = ConnectionMultiplexer.Connect(connectionString);
        _db = multiplexer.GetDatabase(dbIndex);
        _channel = channel;
    }

    private string JobKey(string id, JobStatus status, string userId) => $"channel:{_channel}:job:{id}@{status}:{userId}";
    private string QueueKey(JobStatus status) => $"channel:{_channel}:queue:{status}";

    public IJob CreateJob() => new Job();

    public async Task EnqueueJobAsync<T>(T job) where T : IJob
    {
        if (string.IsNullOrEmpty(job.User?.Id)) throw new ArgumentException("User.Id required");
        var key = JobKey(job.Id, job.Status, job.User.Id);
        await _db.StringSetAsync(key, SerializeJob(job));
        await _db.ListRightPushAsync(QueueKey(job.Status), job.Id);
    }

    public async Task<T> DequeueJobAsync<T>() where T : IJob
    {
        // Pop job data from the QUEUED queue (FIFO)
        var jobData = await _db.ListLeftPopAsync(QueueKey(JobStatus.Queued));
        var jobStr = jobData.ToString();
        if (jobData.HasValue && !string.IsNullOrEmpty(jobStr))
        {
            var job = DeserializeJob<T>(jobStr);
            if (job == null) throw new InvalidOperationException("Failed to deserialize job.");
            // Move job to Running status
            if (string.IsNullOrEmpty(job.User?.Id)) throw new ArgumentException("User.Id required");
            var fromKey = JobKey(job.Id, JobStatus.Queued, job.User.Id);
            var toKey = JobKey(job.Id, JobStatus.Running, job.User.Id);
            await _db.StringSetAsync(toKey, SerializeJob(job));
            await _db.ListRightPushAsync(QueueKey(JobStatus.Running), job.Id);
            await _db.KeyDeleteAsync(fromKey);
            await _db.ListRemoveAsync(QueueKey(JobStatus.Queued), job.Id);
            return job;
        }
        throw new InvalidOperationException("No job available to dequeue.");
    }

    public async Task<T?> LookupJobAsync<T>(string id, JobStatus status, string userId) where T : IJob
    {
        var key = JobKey(id, status, userId);
        var jobData = await _db.StringGetAsync(key);
        var jobStr = jobData.ToString();
        return jobData.HasValue && !string.IsNullOrEmpty(jobStr) ? DeserializeJob<T>(jobStr) : default;
    }

    public async Task<JobStatus> GetJobStatusAsync(string id, string userId)
    {
        foreach (JobStatus status in Enum.GetValues(typeof(JobStatus)))
        {
            var key = JobKey(id, status, userId);
            if (await _db.KeyExistsAsync(key))
                return status;
        }
        throw new KeyNotFoundException("Job not found");
    }

    public async Task<IAsyncEnumerable<T>> ListJobs<T>(string userId, JobStatus status, int batchSize) where T : IJob
    {
        var pattern = JobKey("*", status, userId);
        var server = _db.Multiplexer.GetServer(_db.Multiplexer.GetEndPoints().First());
        var keys = server.Keys(pattern: pattern, pageSize: batchSize).ToList();
        var jobs = new List<T>();
        foreach (var key in keys)
        {
            var jobData = await _db.StringGetAsync(key);
            var jobStr = jobData.ToString();
            if (jobData.HasValue && !string.IsNullOrEmpty(jobStr))
            {
                var job = DeserializeJob<T>(jobStr);
                if (job != null) jobs.Add(job);
            }
        }
        return jobs.ToAsyncEnumerable();
    }

    private string SerializeJob<T>(T job) where T : IJob => System.Text.Json.JsonSerializer.Serialize(job);
    private T? DeserializeJob<T>(string jobData) where T : IJob => System.Text.Json.JsonSerializer.Deserialize<T>(jobData);

    public class Job : IJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public JobStatus Status { get; set; } = JobStatus.Queued;
        public IUser? User { get; set; } = null;
        public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
        public DateTime? StartedAt { get; set; } = null;
        public DateTime? EndedAt { get; set; } = null;
        public string? Error { get; set; } = string.Empty;
        public string? Input { get; set; } = string.Empty;
        public string? Output { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

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
    private string JobIndexKey(string id) => $"channel:{_channel}:job-index:{id}";
    private string RemainingDependenciesKey(string id) => $"channel:{_channel}:job:{id}:dependencies-remaining";
    private string QueueKey(JobStatus status) => $"channel:{_channel}:queue:{status}";
    // Reliable-queue "processing" list: holds the full job keys currently being dispatched by a worker.
    // An entry lingering here after a crash marks an in-flight job that must be recovered.
    private string ProcessingKey() => $"channel:{_channel}:processing";
    private string GraphMutationLockKey() => $"channel:{_channel}:graph-mutation-lock";

    public override IJob CreateJob() => new Job();

    public override Task EnqueueJobAsync<T>(T job) => EnqueueAsync(job);

    public override async Task EnqueueAsync(IJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (HasOutgoingEdges(job))
            throw new ArgumentException("Jobs with outcome callbacks must be submitted as a graph.", nameof(job));

        job.RootJobId = string.IsNullOrWhiteSpace(job.RootJobId) ? job.Id : job.RootJobId;
        if (!StringComparer.Ordinal.Equals(job.RootJobId, job.Id))
            throw new ArgumentException("A standalone job must use its own id as RootJobId.", nameof(job));

        await EnqueueIndependentJobAsync(job);
    }

    public override async Task EnqueueAsync(IJobGraph jobGraph)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(jobGraph);
        var jobs = jobGraph.Jobs;
        if (jobs.Count == 0)
            throw new ArgumentException("At least one job is required.", nameof(jobGraph));
        if (jobs.Count == 1 && !HasOutgoingEdges(jobs.First()))
        {
            await EnqueueAsync(jobs.First());
            return;
        }

        await using var graphLock = await AcquireGraphMutationLockAsync();
        var graph = jobs.ToDictionary(job => job.Id, StringComparer.Ordinal);
        if (graph.Count != jobs.Count)
            throw new ArgumentException("Job ids must be unique within a graph.", nameof(jobGraph));

        var incoming = jobs.ToDictionary(job => job.Id, _ => 0, StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Id))
                throw new ArgumentException("Job id is required.", nameof(jobGraph));
            if (string.IsNullOrWhiteSpace(job.User?.Id))
                throw new ArgumentException($"User.Id is required for job {job.Id}.", nameof(jobGraph));
            if (await FindStoredJobAsync(job.Id) is not null)
                throw new InvalidOperationException($"Job {job.Id} already exists.");

            NormalizeEdges(job.OnSuccess);
            NormalizeEdges(job.OnFailure);
            NormalizeEdges(job.OnCompletion);
            var targets = job.OnSuccess
                .Concat(job.OnFailure)
                .Concat(job.OnCompletion)
                .ToArray();
            if (targets.Length != targets.Distinct(StringComparer.Ordinal).Count())
            {
                throw new ArgumentException(
                    $"Job {job.Id} cannot target the same job for multiple outcomes.",
                    nameof(jobGraph));
            }

            foreach (var targetId in targets)
            {
                if (StringComparer.Ordinal.Equals(targetId, job.Id))
                    throw new ArgumentException($"Job {job.Id} cannot target itself.", nameof(jobGraph));
                if (!graph.ContainsKey(targetId))
                    throw new ArgumentException(
                        $"Job {job.Id} targets {targetId}, which is not part of the graph.",
                        nameof(jobGraph));
                incoming[targetId]++;
            }
        }

        ValidateAcyclicGraph(graph);
        var sinks = jobs.Where(job => !HasOutgoingEdges(job)).ToArray();
        if (sinks.Length != 1)
        {
            throw new ArgumentException(
                "A job graph must have exactly one top-level job with no outgoing callbacks.",
                nameof(jobGraph));
        }

        var rootJobId = sinks[0].Id;
        foreach (var job in jobs)
        {
            if (!string.IsNullOrWhiteSpace(job.RootJobId) &&
                !StringComparer.Ordinal.Equals(job.RootJobId, rootJobId))
            {
                throw new ArgumentException($"Job {job.Id} belongs to a different root.", nameof(jobGraph));
            }

            job.RootJobId = rootJobId;
            job.QueuedAt = DateTime.UtcNow;
            job.StartedAt = null;
            job.EndedAt = null;
            job.Status = incoming[job.Id] == 0 ? JobStatus.Queued : JobStatus.Waiting;
        }

        var transaction = _db!.CreateTransaction();
        foreach (var job in jobs)
        {
            transaction.AddCondition(Condition.KeyNotExists(JobIndexKey(job.Id)));
            var key = JobKey(job.Id, job.Status, job.User!.Id);
            _ = transaction.StringSetAsync(key, SerializeJob(job));
            _ = transaction.StringSetAsync(JobIndexKey(job.Id), key);
            _ = transaction.ListRightPushAsync(QueueKey(job.Status), key);
            if (incoming[job.Id] > 0)
                _ = transaction.StringSetAsync(RemainingDependenciesKey(job.Id), incoming[job.Id]);
        }

        if (!await transaction.ExecuteAsync())
            throw new InvalidOperationException("The job graph changed while it was being enqueued.");
    }

    private async Task EnqueueIndependentJobAsync<T>(T job) where T : class, IJob
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(job.User?.Id))
            throw new ArgumentException("User.Id required", nameof(job));

        job.Status = JobStatus.Queued;
        job.QueuedAt = DateTime.UtcNow;
        job.StartedAt = null;
        job.EndedAt = null;
        var key = JobKey(job.Id, JobStatus.Queued, job.User.Id);
        await _db!.StringSetAsync(key, SerializeJob(job));
        await _db.ListRightPushAsync(QueueKey(JobStatus.Queued), key);
    }

    private static void NormalizeEdges(List<string> edges)
    {
        var normalized = edges
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        edges.Clear();
        edges.AddRange(normalized);
    }

    private static void ValidateAcyclicGraph(IReadOnlyDictionary<string, IJob> graph)
    {
        var inDegree = graph.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var job in graph.Values)
        {
            foreach (var targetId in AllTargets(job))
                inDegree[targetId]++;
        }

        var ready = new Queue<string>(inDegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;

        while (ready.TryDequeue(out var id))
        {
            visited++;
            foreach (var targetId in AllTargets(graph[id]))
            {
                inDegree[targetId]--;
                if (inDegree[targetId] == 0)
                    ready.Enqueue(targetId);
            }
        }

        if (visited != graph.Count)
            throw new ArgumentException("Job dependencies must form an acyclic graph.", nameof(graph));
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
        if (IsGraphJob(job))
            _ = transaction.StringSetAsync(JobIndexKey(job.Id), runningKey);
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

    private async Task<StoredJob?> FindStoredJobAsync(string id)
    {
        EnsureInitialized();
        var indexedKey = await _db!.StringGetAsync(JobIndexKey(id));
        if (indexedKey.HasValue)
        {
            var key = indexedKey.ToString();
            var data = await _db.StringGetAsync(key);
            if (data.HasValue)
                return new StoredJob(DeserializeJob<Job>(data.ToString()), key);
        }

        var server = _db.Multiplexer.GetServer(_db.Multiplexer.GetEndPoints().First());
        var pattern = $"channel:{_channel}:job:{id}@*:*";
        var fallbackKey = server.Keys(pattern: pattern).FirstOrDefault();
        if (fallbackKey == default(RedisKey))
            return null;

        var fallbackData = await _db.StringGetAsync(fallbackKey);
        if (!fallbackData.HasValue)
            return null;

        var fallbackKeyString = fallbackKey.ToString();
        await _db.StringSetAsync(JobIndexKey(id), fallbackKeyString);
        return new StoredJob(DeserializeJob<Job>(fallbackData.ToString()), fallbackKeyString);
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
        var oldStatus = job.Status;
        if (!await TransitionJobAsync(job, oldStatus, newStatus, error, output))
            throw new InvalidOperationException($"Job {job.Id} is no longer in the {oldStatus} state.");
    }

    public override async Task<bool> UpdateJobStatusAsync(string jobId, JobStatus fromStatus, JobStatus toStatus, string userId, string? error = null, string? output = null)
    {
        EnsureInitialized();
        Job job;
        try
        {
            job = await LookupJobAsync<Job>(jobId, fromStatus, userId);
        }
        catch (KeyNotFoundException)
        {
            return false;
        }

        return await TransitionJobAsync(job, fromStatus, toStatus, error, output);
    }

    private async Task<bool> TransitionJobAsync(
        IJob job,
        JobStatus fromStatus,
        JobStatus toStatus,
        string? error,
        string? output)
    {
        if (string.IsNullOrWhiteSpace(job.User?.Id))
            return false;
        if (fromStatus == JobStatus.Waiting &&
            toStatus is not JobStatus.Failed and not JobStatus.Canceled)
        {
            return false;
        }

        var terminal = toStatus is
            JobStatus.Completed or
            JobStatus.Failed or
            JobStatus.Canceled or
            JobStatus.Skipped;
        if (!terminal || !HasOutgoingEdges(job))
            return await TransitionSingleJobAsync(job, fromStatus, toStatus, error, output);

        await using var graphLock = await AcquireGraphMutationLockAsync();
        return await TransitionGraphJobAsync(job, fromStatus, toStatus, error, output);
    }

    private async Task<bool> TransitionSingleJobAsync(
        IJob job,
        JobStatus fromStatus,
        JobStatus toStatus,
        string? error,
        string? output)
    {
        var userId = job.User!.Id;
        var fromKey = JobKey(job.Id, fromStatus, userId);
        var toKey = JobKey(job.Id, toStatus, userId);
        ApplyTransition(job, toStatus, error, output);

        var transaction = _db!.CreateTransaction();
        transaction.AddCondition(Condition.KeyExists(fromKey));
        QueueTransition(transaction, job, fromStatus, fromKey, toStatus, toKey);
        return await transaction.ExecuteAsync();
    }

    private async Task<bool> TransitionGraphJobAsync(
        IJob job,
        JobStatus fromStatus,
        JobStatus toStatus,
        string? error,
        string? output)
    {
        var userId = job.User!.Id;
        var fromKey = JobKey(job.Id, fromStatus, userId);
        var toKey = JobKey(job.Id, toStatus, userId);
        ApplyTransition(job, toStatus, error, output);

        var resolutions = new Queue<(IJob Source, JobStatus Status)>();
        resolutions.Enqueue((job, toStatus));
        var storedJobs = new Dictionary<string, StoredJob>(StringComparer.Ordinal);
        var remaining = new Dictionary<string, long>(StringComparer.Ordinal);
        var transitions = new Dictionary<string, PendingJobTransition>(StringComparer.Ordinal);

        while (resolutions.TryDequeue(out var resolution))
        {
            foreach (var targetId in UnselectedTargets(resolution.Source, resolution.Status))
            {
                if (transitions.TryGetValue(targetId, out var existing) &&
                    existing.Status == JobStatus.Skipped)
                {
                    continue;
                }

                var stored = await GetWaitingJobAsync(targetId, storedJobs);
                if (stored is null)
                    continue;

                var skipped = stored.Job;
                skipped.Status = JobStatus.Skipped;
                skipped.EndedAt = DateTime.UtcNow;
                skipped.Error = $"Callback condition from {resolution.Source.Id} was not selected.";
                var transition = new PendingJobTransition(
                    skipped,
                    stored.Key,
                    JobStatus.Skipped,
                    JobKey(skipped.Id, JobStatus.Skipped, skipped.User!.Id));
                transitions[targetId] = transition;
                remaining.Remove(targetId);
                resolutions.Enqueue((skipped, JobStatus.Skipped));
            }

            foreach (var targetId in SelectedTargets(resolution.Source, resolution.Status))
            {
                if (transitions.ContainsKey(targetId))
                    continue;

                var stored = await GetWaitingJobAsync(targetId, storedJobs);
                if (stored is null)
                    continue;

                if (!remaining.TryGetValue(targetId, out var count))
                {
                    var value = await _db!.StringGetAsync(RemainingDependenciesKey(targetId));
                    count = value.HasValue ? (long)value : 0;
                }

                count--;
                if (count <= 0)
                {
                    var ready = stored.Job;
                    ready.Status = JobStatus.Queued;
                    ready.StartedAt = null;
                    ready.EndedAt = null;
                    transitions[targetId] = new PendingJobTransition(
                        ready,
                        stored.Key,
                        JobStatus.Queued,
                        JobKey(ready.Id, JobStatus.Queued, ready.User!.Id));
                    remaining.Remove(targetId);
                }
                else
                {
                    remaining[targetId] = count;
                }
            }
        }

        var transaction = _db!.CreateTransaction();
        transaction.AddCondition(Condition.KeyExists(fromKey));
        QueueTransition(transaction, job, fromStatus, fromKey, toStatus, toKey);

        foreach (var pair in remaining)
            _ = transaction.StringSetAsync(RemainingDependenciesKey(pair.Key), pair.Value);

        foreach (var change in transitions.Values)
        {
            transaction.AddCondition(Condition.KeyExists(change.WaitingKey));
            _ = transaction.StringSetAsync(change.NewKey, SerializeJob(change.Job));
            _ = transaction.StringSetAsync(JobIndexKey(change.Job.Id), change.NewKey);
            _ = transaction.ListRightPushAsync(QueueKey(change.Status), change.NewKey);
            _ = transaction.KeyDeleteAsync(change.WaitingKey);
            _ = transaction.ListRemoveAsync(QueueKey(JobStatus.Waiting), change.WaitingKey);
            _ = transaction.KeyDeleteAsync(RemainingDependenciesKey(change.Job.Id));
        }

        return await transaction.ExecuteAsync();
    }

    private void QueueTransition(
        ITransaction transaction,
        IJob job,
        JobStatus fromStatus,
        string fromKey,
        JobStatus toStatus,
        string toKey)
    {
        _ = transaction.StringSetAsync(toKey, SerializeJob(job));
        if (IsGraphJob(job))
            _ = transaction.StringSetAsync(JobIndexKey(job.Id), toKey);
        _ = transaction.ListRightPushAsync(QueueKey(toStatus), toKey);
        _ = transaction.KeyDeleteAsync(fromKey);
        _ = transaction.ListRemoveAsync(QueueKey(fromStatus), fromKey);
        _ = transaction.ListRemoveAsync(ProcessingKey(), fromKey);
    }

    private static void ApplyTransition(
        IJob job,
        JobStatus toStatus,
        string? error,
        string? output)
    {
        job.Status = toStatus;
        job.EndedAt = toStatus is
            JobStatus.Completed or
            JobStatus.Failed or
            JobStatus.Canceled or
            JobStatus.Skipped
            ? DateTime.UtcNow
            : null;
        if (!string.IsNullOrEmpty(error))
            job.Error = error;
        if (!string.IsNullOrEmpty(output))
            job.Output = output;
    }

    private static bool IsGraphJob(IJob job) =>
        HasOutgoingEdges(job) || !StringComparer.Ordinal.Equals(job.RootJobId, job.Id);

    private static bool HasOutgoingEdges(IJob job) =>
        job.OnSuccess.Count > 0 ||
        job.OnFailure.Count > 0 ||
        job.OnCompletion.Count > 0;

    private static IEnumerable<string> AllTargets(IJob job) =>
        job.OnSuccess
            .Concat(job.OnFailure)
            .Concat(job.OnCompletion)
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> SelectedTargets(IJob job, JobStatus status)
    {
        var outcomeTargets = status switch
        {
            JobStatus.Completed => job.OnSuccess,
            JobStatus.Failed or JobStatus.Canceled => job.OnFailure,
            JobStatus.Skipped => [],
            _ => []
        };
        return outcomeTargets
            .Concat(job.OnCompletion)
            .Distinct(StringComparer.Ordinal);
    }

    private static IEnumerable<string> UnselectedTargets(IJob job, JobStatus status) =>
        status switch
        {
            JobStatus.Completed => job.OnFailure,
            JobStatus.Failed or JobStatus.Canceled => job.OnSuccess,
            JobStatus.Skipped => job.OnSuccess.Concat(job.OnFailure).Distinct(StringComparer.Ordinal),
            _ => []
        };

    private async Task<StoredJob?> GetWaitingJobAsync(
        string id,
        IDictionary<string, StoredJob> cache)
    {
        if (!cache.TryGetValue(id, out var stored))
        {
            stored = await FindStoredJobAsync(id);
            if (stored is null)
                return null;
            cache[id] = stored;
        }

        return stored.Job.Status == JobStatus.Waiting ? stored : null;
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
        if (IsGraphJob(job))
            _ = transaction.StringSetAsync(JobIndexKey(job.Id), queuedKey);
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

    private async Task<RedisGraphLock> AcquireGraphMutationLockAsync()
    {
        EnsureInitialized();
        var token = Guid.NewGuid().ToString("N");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await _db!.LockTakeAsync(GraphMutationLockKey(), token, TimeSpan.FromMinutes(1)))
                return new RedisGraphLock(_db, GraphMutationLockKey(), token);

            await Task.Delay(20);
        }

        throw new TimeoutException("Timed out waiting for the job graph mutation lock.");
    }

    private sealed record StoredJob(Job Job, string Key);
    private sealed record PendingJobTransition(
        Job Job,
        string WaitingKey,
        JobStatus Status,
        string NewKey);

    private sealed class RedisGraphLock(
        IDatabase database,
        RedisKey key,
        RedisValue token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await database.LockReleaseAsync(key, token);
        }
    }
}

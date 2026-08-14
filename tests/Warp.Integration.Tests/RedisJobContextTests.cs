using Testcontainers.Redis;
using Warp.Core.Data.Contexts;
using Warp.Core.Job;
using Warp.Core.Job.Contexts;

namespace Warp.Integration.Tests;

/// <summary>
/// Reliable-queue guarantees for <see cref="RedisJobContext"/> (the hardening from #28), proven
/// against a real Redis container:
///   - enqueue → atomic LMOVE dequeue into the processing list (job becomes Running),
///   - UpdateJobStatusAsync CAS transitions where only the first of many parallel callers wins,
///   - RecoverProcessingJobsAsync requeues an orphaned in-flight job (at-least-once).
/// </summary>
public sealed class RedisJobContextTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    private RedisJobContext _ctx = null!;
    private const string Channel = "test";

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _ctx = new RedisJobContext();
        _ctx.Initialize(_redis.GetConnectionString(), Channel);
    }

    public async Task DisposeAsync() => await _redis.DisposeAsync();

    private static Job NewJob(out string userId)
    {
        userId = $"user-{Guid.NewGuid():N}";
        var user = new SqliteDataContext.User { Id = userId, Email = "j@warp.test" };
        return new Job { Id = $"job-{Guid.NewGuid():N}", User = user, Status = JobStatus.Queued };
    }

    [Fact]
    public async Task Enqueue_then_dequeue_transitions_to_running_via_reliable_queue()
    {
        var job = NewJob(out var userId);
        await _ctx.EnqueueJobAsync(job);

        var dequeued = await _ctx.DequeueJobAsync<Job>();

        dequeued.HasJob.Should().BeTrue("the enqueued job must be reliably claimed");
        dequeued.Job!.Id.Should().Be(job.Id);
        dequeued.Job.Status.Should().Be(JobStatus.Running, "dequeue moves the job Queued -> Running");
        (await _ctx.GetJobStatusAsync(job.Id, userId)).Should().Be(JobStatus.Running);
    }

    [Fact]
    public async Task Parallel_status_transition_only_first_caller_wins()
    {
        var job = NewJob(out var userId);
        await _ctx.EnqueueJobAsync(job);
        var dequeued = await _ctx.DequeueJobAsync<Job>();
        dequeued.HasJob.Should().BeTrue();

        // 50 racers all try Running -> Completed; the CAS (AddCondition KeyExists) must let exactly one win.
        const int racers = 50;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, racers).Select(async _ =>
        {
            await gate.Task;
            return await _ctx.UpdateJobStatusAsync(job.Id, JobStatus.Running, JobStatus.Completed, userId);
        }).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        results.Count(won => won).Should().Be(1, "only the first CAS transition may succeed");
        (await _ctx.GetJobStatusAsync(job.Id, userId)).Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task Recover_requeues_orphaned_in_flight_job()
    {
        var job = NewJob(out var userId);
        await _ctx.EnqueueJobAsync(job);

        // Dequeue leaves the job Running and tracked on the processing list. Simulating a worker
        // crash = never completing it, then running recovery.
        var dequeued = await _ctx.DequeueJobAsync<Job>();
        dequeued.HasJob.Should().BeTrue();

        var recovered = await _ctx.RecoverProcessingJobsAsync<Job>(maxAttempts: 3);

        recovered.Should().Be(1, "the orphaned in-flight job must be recovered");
        (await _ctx.GetJobStatusAsync(job.Id, userId)).Should().Be(JobStatus.Queued,
            "recovery requeues the job (under the attempt cap) so it is never lost");

        // And it can be claimed again (at-least-once).
        var reclaimed = await _ctx.DequeueJobAsync<Job>();
        reclaimed.HasJob.Should().BeTrue();
        reclaimed.Job!.Id.Should().Be(job.Id);
        reclaimed.Job.Attempts.Should().Be(1, "the interrupted attempt was counted");
    }
}

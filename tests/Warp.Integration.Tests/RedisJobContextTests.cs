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
    private RedisContainer? _redis;
    private RedisJobContext _ctx = null!;
    private const string Channel = "test";

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("WARP_TEST_REDIS_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _redis = new RedisBuilder()
                .WithImage("redis:7-alpine")
                .Build();
            await _redis.StartAsync();
            connectionString = _redis.GetConnectionString();
        }

        _ctx = new RedisJobContext();
        _ctx.Initialize(connectionString, $"{Channel}-{Guid.NewGuid():N}");
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
            await _redis.DisposeAsync();
    }

    private static Job NewJob(out string userId)
    {
        userId = $"user-{Guid.NewGuid():N}";
        var user = new SqliteDataContext.User { Id = userId, Email = "j@warp.test" };
        return new Job { Id = $"job-{Guid.NewGuid():N}", User = user, Status = JobStatus.Queued };
    }

    private static JobGraphNode ForUser(
        JobGraphNode node,
        SqliteDataContext.User user)
    {
        node.Job.User = user;
        return node;
    }

    [Fact]
    public async Task Enqueue_then_dequeue_transitions_to_running_via_reliable_queue()
    {
        var job = NewJob(out var userId);
        await _ctx.EnqueueAsync(job);

        var dequeued = await _ctx.DequeueJobAsync<Job>();

        dequeued.HasJob.Should().BeTrue("the enqueued job must be reliably claimed");
        dequeued.Job!.Id.Should().Be(job.Id);
        dequeued.Job.Status.Should().Be(JobStatus.Running, "dequeue moves the job Queued -> Running");
        dequeued.Job.RootJobId.Should().Be(job.Id, "standalone jobs are their own graph root");
        (await _ctx.GetJobStatusAsync(job.Id, userId)).Should().Be(JobStatus.Running);
    }

    [Fact]
    public async Task Graph_routes_success_and_unlocks_fan_in_only_after_every_selected_edge()
    {
        var user = new SqliteDataContext.User
        {
            Id = $"user-{Guid.NewGuid():N}",
            Email = "dag@warp.test"
        };
        var graph = _ctx.CreateGraph();
        var first = ForUser(graph.Add(), user);
        var left = ForUser(first.OnSuccess(), user);
        var right = ForUser(first.OnSuccess(), user);
        var final = ForUser(left.OnSuccess(), user);
        right.OnSuccess(final);

        await _ctx.EnqueueAsync(graph);

        first.Job.RootJobId.Should().Be(final.Job.Id);
        left.Job.RootJobId.Should().Be(final.Job.Id);
        right.Job.RootJobId.Should().Be(final.Job.Id);
        final.Job.RootJobId.Should().Be(final.Job.Id);
        (await _ctx.GetJobStatusAsync(first.Job.Id, user.Id)).Should().Be(JobStatus.Queued);
        (await _ctx.GetJobStatusAsync(left.Job.Id, user.Id)).Should().Be(JobStatus.Waiting);
        (await _ctx.GetJobStatusAsync(right.Job.Id, user.Id)).Should().Be(JobStatus.Waiting);
        (await _ctx.GetJobStatusAsync(final.Job.Id, user.Id)).Should().Be(JobStatus.Waiting);
        (await _ctx.UpdateJobStatusAsync(
            left.Job.Id,
            JobStatus.Waiting,
            JobStatus.Running,
            user.Id)).Should().BeFalse("waiting jobs can only be unlocked by their dependencies");

        var claimedFirst = await _ctx.DequeueJobAsync<Job>();
        claimedFirst.Job!.Id.Should().Be(first.Job.Id);
        await _ctx.UpdateJobStatusAsync(first.Job.Id, JobStatus.Running, JobStatus.Completed, user.Id);

        var unlocked = new[]
        {
            (await _ctx.DequeueJobAsync<Job>()).Job!,
            (await _ctx.DequeueJobAsync<Job>()).Job!
        };
        unlocked.Select(job => job.Id).Should().BeEquivalentTo([left.Job.Id, right.Job.Id]);
        (await _ctx.GetJobStatusAsync(final.Job.Id, user.Id)).Should().Be(JobStatus.Waiting);

        await _ctx.UpdateJobStatusAsync(unlocked[0].Id, JobStatus.Running, JobStatus.Completed, user.Id);
        (await _ctx.GetJobStatusAsync(final.Job.Id, user.Id)).Should().Be(JobStatus.Waiting);
        await _ctx.UpdateJobStatusAsync(unlocked[1].Id, JobStatus.Running, JobStatus.Completed, user.Id);

        var claimedFinal = await _ctx.DequeueJobAsync<Job>();
        claimedFinal.Job!.Id.Should().Be(final.Job.Id);
    }

    [Fact]
    public async Task Graph_rejects_cycles_before_any_job_is_visible()
    {
        var user = new SqliteDataContext.User
        {
            Id = $"user-{Guid.NewGuid():N}",
            Email = "cycle@warp.test"
        };
        var graph = _ctx.CreateGraph();
        var first = ForUser(graph.Add(), user);
        var second = ForUser(first.OnSuccess(), user);
        second.OnSuccess(first);

        var action = () => _ctx.EnqueueAsync(graph);

        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*acyclic*");
        var dequeue = await _ctx.DequeueJobAsync<Job>();
        dequeue.HasJob.Should().BeFalse();
    }

    [Fact]
    public async Task Graph_requires_one_top_level_job()
    {
        var user = new SqliteDataContext.User
        {
            Id = $"user-{Guid.NewGuid():N}",
            Email = "roots@warp.test"
        };
        var graph = _ctx.CreateGraph();
        _ = ForUser(graph.Add(), user);
        _ = ForUser(graph.Add(), user);

        var action = () => _ctx.EnqueueAsync(graph);

        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*exactly one top-level job*");
    }

    [Fact]
    public async Task Failure_route_runs_while_success_route_is_skipped_then_rejoins_on_completion()
    {
        var user = new SqliteDataContext.User
        {
            Id = $"user-{Guid.NewGuid():N}",
            Email = "failure@warp.test"
        };
        var graph = _ctx.CreateGraph();
        var first = ForUser(graph.Add(), user);
        var normal = ForUser(first.OnSuccess(), user);
        var recovery = ForUser(first.OnFailure(), user);
        var final = ForUser(normal.OnCompletion(), user);
        recovery.OnCompletion(final);

        await _ctx.EnqueueAsync(graph);
        var claimed = await _ctx.DequeueJobAsync<Job>();
        claimed.Job!.Id.Should().Be(first.Job.Id);

        var failed = await _ctx.UpdateJobStatusAsync(
            first.Job.Id,
            JobStatus.Running,
            JobStatus.Failed,
            user.Id,
            error: "first failed");

        failed.Should().BeTrue();
        (await _ctx.GetJobStatusAsync(normal.Job.Id, user.Id)).Should().Be(JobStatus.Skipped);
        (await _ctx.GetJobStatusAsync(recovery.Job.Id, user.Id)).Should().Be(JobStatus.Queued);
        (await _ctx.GetJobStatusAsync(final.Job.Id, user.Id)).Should().Be(JobStatus.Waiting);

        var claimedRecovery = await _ctx.DequeueJobAsync<Job>();
        claimedRecovery.Job!.Id.Should().Be(recovery.Job.Id);
        await _ctx.UpdateJobStatusAsync(
            recovery.Job.Id,
            JobStatus.Running,
            JobStatus.Completed,
            user.Id);

        (await _ctx.DequeueJobAsync<Job>()).Job!.Id.Should().Be(final.Job.Id);
    }

    [Fact]
    public async Task Duplicate_completion_does_not_unlock_a_dependent_twice()
    {
        var user = new SqliteDataContext.User
        {
            Id = $"user-{Guid.NewGuid():N}",
            Email = "duplicate@warp.test"
        };
        var graph = _ctx.CreateGraph();
        var first = ForUser(graph.Add(), user);
        var second = ForUser(first.OnSuccess(), user);

        await _ctx.EnqueueAsync(graph);
        _ = await _ctx.DequeueJobAsync<Job>();

        (await _ctx.UpdateJobStatusAsync(
            first.Job.Id,
            JobStatus.Running,
            JobStatus.Completed,
            user.Id)).Should().BeTrue();
        (await _ctx.UpdateJobStatusAsync(
            first.Job.Id,
            JobStatus.Running,
            JobStatus.Completed,
            user.Id)).Should().BeFalse();

        (await _ctx.DequeueJobAsync<Job>()).Job!.Id.Should().Be(second.Job.Id);
        (await _ctx.DequeueJobAsync<Job>()).HasJob.Should().BeFalse();
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

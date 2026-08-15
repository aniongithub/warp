using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Redis;
using Warp.Core.Data;
using Warp.Core.Data.Contexts;
using Warp.Core.Job;
using Warp.Core.Job.Contexts;
using Warp.Dilithium.Middleware;

namespace Warp.Integration.Tests;

/// <summary>
/// Regression coverage for the async RESULT path in <see cref="AsyncApiHandler{TOptions,TJobContext}"/>.
///
/// The old <c>GetJobResultAsync</c> looped over every <see cref="JobStatus"/> and expected
/// <c>LookupJobAsync</c> to return <c>null</c> on a miss. But <see cref="RedisJobContext.LookupJobAsync{T}"/>
/// THROWS <see cref="KeyNotFoundException"/> on a miss, so the loop blew up on its very first
/// (Queued) iteration for any job that had moved past Queued — breaking the result endpoint for a
/// job that had actually completed. The fix resolves the job's current status first
/// (<c>GetJobStatusAsync</c>) and then does a single lookup, mirroring <c>CancelJobAsync</c>.
///
/// Driven against a real Redis container through the real handler method.
/// </summary>
public sealed class AsyncApiResultTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    private RedisJobContext _ctx = null!;
    private TestAsyncApiHandler _handler = null!;
    private string _dbPath = null!;
    private const string Channel = "test";

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        var connectionString = _redis.GetConnectionString();

        _ctx = new RedisJobContext();
        _ctx.Initialize(connectionString, Channel);

        _dbPath = Path.Combine(Path.GetTempPath(), $"warp-asyncresult-{Guid.NewGuid():N}.db");
        var options = new TestAsyncApiHandlerOptions
        {
            ConnectionString = connectionString,
            Channel = Channel
        };
        // The handler builds its own RedisJobContext from options.ConnectionString, pointed at the
        // same container as _ctx, so state written via _ctx is visible to the handler.
        _handler = new TestAsyncApiHandler(NullLogger.Instance, new SqliteDataContext(_dbPath), options);
    }

    public async Task DisposeAsync()
    {
        _handler.Dispose();
        await _redis.DisposeAsync();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private static Job NewJob(out string userId)
    {
        userId = $"user-{Guid.NewGuid():N}";
        var user = new SqliteDataContext.User { Id = userId, Email = "j@warp.test" };
        return new Job { Id = $"job-{Guid.NewGuid():N}", User = user, Status = JobStatus.Queued };
    }

    [Fact]
    public async Task GetJobResult_returns_completed_job_when_earlier_statuses_are_absent()
    {
        var job = NewJob(out var userId);
        await _ctx.EnqueueJobAsync(job);

        var dequeued = await _ctx.DequeueJobAsync<Job>(); // Queued -> Running
        dequeued.HasJob.Should().BeTrue();

        (await _ctx.UpdateJobStatusAsync(job.Id, JobStatus.Running, JobStatus.Completed, userId, output: "the-answer"))
            .Should().BeTrue("the job must end up stored under Completed");

        // The completed job now lives ONLY under Completed. Looking it up under an earlier status
        // THROWS (the exact contract the old GetJobResultAsync loop wrongly assumed returned null,
        // which made it blow up on its first Queued iteration).
        await FluentActions
            .Awaiting(() => _ctx.LookupJobAsync<Job>(job.Id, JobStatus.Queued, userId))
            .Should().ThrowAsync<KeyNotFoundException>("a non-matching status lookup throws, not returns null");

        // Before the fix this threw KeyNotFoundException on the Queued iteration; after the fix it
        // resolves the current status (Completed) first and returns the real result.
        var result = await _handler.InvokeGetJobResultAsync(job.Id, userId);

        result.Should().NotBeNull();
        result.JobId.Should().Be(job.Id);
        result.Status.Should().Be(JobStatus.Completed);
        result.Output.Should().Be("the-answer");
        result.QueuedAt.Should().Be(job.QueuedAt);
    }

    [Fact]
    public async Task GetJobResult_throws_KeyNotFound_for_a_genuinely_missing_job()
    {
        await FluentActions
            .Awaiting(() => _handler.InvokeGetJobResultAsync($"missing-{Guid.NewGuid():N}", $"user-{Guid.NewGuid():N}"))
            .Should().ThrowAsync<KeyNotFoundException>("a job that exists in no status must still surface as not-found");
    }

    private sealed class TestAsyncApiHandlerOptions : AsyncApiHandlerOptions
    {
    }

    /// <summary>Thin subclass that exposes the protected <c>GetJobResultAsync</c> for direct testing.</summary>
    private sealed class TestAsyncApiHandler : AsyncApiHandler<TestAsyncApiHandlerOptions, RedisJobContext>
    {
        public TestAsyncApiHandler(ILogger logger, IDataContext context, TestAsyncApiHandlerOptions options)
            : base("test-async", logger, context, options)
        {
        }

        public Task<JobResult> InvokeGetJobResultAsync(string jobId, string userId)
            => GetJobResultAsync(jobId, userId);
    }
}

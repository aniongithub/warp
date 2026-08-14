using Warp.Core.Data;
using Warp.Integration.Tests.Infrastructure;

namespace Warp.Integration.Tests;

/// <summary>
/// The backend-parametrized atomicity/concurrency suite. The SAME assertions run against every
/// <see cref="IDataContextBackend"/> so the hardening is guarded identically across Json, Sqlite,
/// PostgreSql and Firestore. Adding a backend = implement <see cref="IDataContextBackend"/> and
/// add a one-line derived class at the bottom of this file.
///
/// TODO(#32): when Grant/Settle land on IDataContext, add:
///   - Grant: N parallel GrantQuotaAsync; assert final Limit == sum, no lost updates.
///   - Settle: interleave reserve (TryConsumeQuotaAsync) + SettleQuotaAsync(delta); assert Used
///     reconciles exactly and never goes negative.
/// These are deliberately omitted here because PR #32 is not yet merged.
/// </summary>
public abstract class AtomicityTestsBase<TBackend> : IClassFixture<TBackend>
    where TBackend : class, IDataContextBackend
{
    private readonly TBackend _backend;

    protected AtomicityTestsBase(TBackend backend) => _backend = backend;

    /// <summary>
    /// N parallel prepaid TryConsumeQuotaAsync against a limit smaller than N. Asserts the final
    /// Used is EXACTLY the limit (no lost updates), never overruns, and LimitExceeded is returned
    /// exactly the right number of times.
    /// </summary>
    [Fact]
    public async Task Prepaid_quota_consume_is_exact_under_parallelism()
    {
        var ctx = _backend.Context;
        var n = _backend.Parallelism;
        const float amount = 1f;
        var limit = n / 2;               // exactly half the calls should succeed
        var expectedExceeded = n - limit;

        var id = $"quota-{Guid.NewGuid():N}";
        var quota = ctx.CreateQuota();
        quota.Id = id;
        quota.Key = $"key-{id}";
        quota.QuotaName = "atomicity-test";
        quota.Used = 0;
        quota.Limit = limit;
        quota.Type = "prepaid";
        await ctx.SaveAsync(quota);

        var results = await ParallelRunner.RunAllAsync(
            n, _ => _backend.ExecuteAsync(() => ctx.TryConsumeQuotaAsync(id, amount)));

        var consumed = results.Count(r => r == QuotaConsumeResult.Consumed);
        var exceeded = results.Count(r => r == QuotaConsumeResult.LimitExceeded);
        var notFound = results.Count(r => r == QuotaConsumeResult.NotFound);

        notFound.Should().Be(0, "the quota exists for every call");
        consumed.Should().Be(limit, $"[{_backend.Name}] exactly Limit consumes must succeed with no lost updates");
        exceeded.Should().Be(expectedExceeded, $"[{_backend.Name}] every other call must be rejected as LimitExceeded");

        var finalUsed = ctx.Quotas.First(q => q.Id == id).Used;
        finalUsed.Should().Be(limit, $"[{_backend.Name}] final Used must equal Limit exactly");
        finalUsed.Should().BeLessThanOrEqualTo(limit, $"[{_backend.Name}] a prepaid quota must never overrun its Limit");
    }

    /// <summary>
    /// N parallel TryConsumeRateLimitAsync at a FIXED instant against a bucket of capacity C.
    /// The safety invariant is that the number of allowed requests never exceeds capacity.
    /// </summary>
    [Fact]
    public async Task Rate_limit_never_exceeds_capacity_under_parallelism()
    {
        var ctx = _backend.Context;
        var n = _backend.Parallelism;
        const float capacity = 10f;
        const float rateLimitHz = 1f;

        // Faithful to the production caller (RateLimiter passes DateTime.UtcNow, Kind=Utc). Using a
        // single fixed instant for every call means no refill happens between calls, so the allowed
        // count is bounded by the bucket capacity.
        var now = DateTime.UtcNow;
        var key = $"rl-{Guid.NewGuid():N}";

        var results = await ParallelRunner.RunAllAsync(
            n, _ => _backend.ExecuteAsync(() => ctx.TryConsumeRateLimitAsync(key, rateLimitHz, capacity, now)));

        var allowed = results.Count(r => r);

        allowed.Should().BeLessThanOrEqualTo((int)capacity,
            $"[{_backend.Name}] the number of allowed requests must never exceed the bucket capacity");
        allowed.Should().BeGreaterThan(0, $"[{_backend.Name}] at least one request must be allowed");
    }
}

// ----- One derived class per backend. Adding a backend is one line. -----

public sealed class JsonAtomicityTests : AtomicityTestsBase<JsonBackend>
{
    public JsonAtomicityTests(JsonBackend backend) : base(backend) { }
}

public sealed class SqliteAtomicityTests : AtomicityTestsBase<SqliteBackend>
{
    public SqliteAtomicityTests(SqliteBackend backend) : base(backend) { }
}

public sealed class PostgreSqlAtomicityTests : AtomicityTestsBase<PostgreSqlBackend>
{
    public PostgreSqlAtomicityTests(PostgreSqlBackend backend) : base(backend) { }
}

public sealed class FirestoreAtomicityTests : AtomicityTestsBase<FirestoreBackend>
{
    public FirestoreAtomicityTests(FirestoreBackend backend) : base(backend) { }
}

using Warp.Core.Data;
using Warp.Integration.Tests.Infrastructure;

namespace Warp.Integration.Tests;

/// <summary>
/// The backend-parametrized atomicity/concurrency suite. The SAME assertions run against every
/// <see cref="IDataContextBackend"/> so the hardening is guarded identically across Json, Sqlite,
/// PostgreSql and Firestore. Adding a backend = implement <see cref="IDataContextBackend"/> and
/// add a one-line derived class at the bottom of this file.
///
/// Grant/Settle (PR #32) are now on <see cref="IDataContext"/>, so this suite also asserts:
///   - Grant (money-in): N parallel <see cref="IDataContext.GrantQuotaAsync"/>; final Limit ==
///     starting + sum, with no lost updates.
///   - Settle (reconcile): interleave reserve (<see cref="IDataContext.TryConsumeQuotaAsync"/>) +
///     <see cref="IDataContext.SettleQuotaAsync"/>(delta); Used reconciles to the exact expected
///     value and never goes negative.
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
    /// N parallel grants (money-in). Each <see cref="IDataContext.GrantQuotaAsync"/> adds a fixed
    /// amount to <c>Limit</c>. Asserts the final Limit equals the starting Limit plus the sum of all
    /// grants — i.e. no concurrent read-modify-write loses an update on the credit-granting path.
    /// </summary>
    [Fact]
    public async Task Grant_is_exact_under_parallelism()
    {
        var ctx = _backend.Context;
        var n = _backend.Parallelism;
        const float startingLimit = 100f;
        const float grant = 3f;

        var id = $"quota-{Guid.NewGuid():N}";
        var quota = ctx.CreateQuota();
        quota.Id = id;
        quota.Key = $"key-{id}";
        quota.QuotaName = "atomicity-grant";
        quota.Used = 0;
        quota.Limit = startingLimit;
        quota.Type = "prepaid";
        await ctx.SaveAsync(quota);

        await ParallelRunner.RunAllAsync(n, _ => _backend.ExecuteAsync(async () =>
        {
            await ctx.GrantQuotaAsync(id, grant);
            return true;
        }));

        var expected = startingLimit + n * grant;
        var finalLimit = ctx.Quotas.First(q => q.Id == id).Limit;
        finalLimit.Should().Be(expected,
            $"[{_backend.Name}] every grant must be applied exactly once — final Limit == starting + sum(grants), no lost updates");
    }

    /// <summary>
    /// N parallel reserve+settle cycles modelling the production admission/reconciliation flow.
    /// Each operation reserves <c>reserve</c> units up front via <see cref="IDataContext.TryConsumeQuotaAsync"/>,
    /// then reconciles the difference against the actual usage via
    /// <see cref="IDataContext.SettleQuotaAsync"/> (<c>delta = actual - reserve</c>, here negative).
    /// The reserves and settles from different operations interleave freely, yet the final <c>Used</c>
    /// must reconcile to exactly <c>N * actual</c> with no lost updates and must never go negative.
    /// </summary>
    [Fact]
    public async Task Settle_reconciles_reservations_exactly_under_parallelism()
    {
        var ctx = _backend.Context;
        var n = _backend.Parallelism;
        const float reserve = 2f;   // reserved up-front at admission
        const float actual = 1f;    // real usage known only after the response
        const float settleDelta = actual - reserve; // -1: release the unused portion

        // Limit is set so that even if every reservation is taken before any settle runs, the peak
        // Used (n * reserve) still fits — so every reserve must succeed and the only thing under test
        // is that the settles reconcile exactly.
        var limit = n * reserve;

        var id = $"quota-{Guid.NewGuid():N}";
        var quota = ctx.CreateQuota();
        quota.Id = id;
        quota.Key = $"key-{id}";
        quota.QuotaName = "atomicity-settle";
        quota.Used = 0;
        quota.Limit = limit;
        quota.Type = "prepaid";
        await ctx.SaveAsync(quota);

        var results = await ParallelRunner.RunAllAsync(n, async _ =>
        {
            var consume = await _backend.ExecuteAsync(() => ctx.TryConsumeQuotaAsync(id, reserve));
            await _backend.ExecuteAsync(async () =>
            {
                await ctx.SettleQuotaAsync(id, settleDelta);
                return true;
            });
            return consume;
        });

        results.Count(r => r == QuotaConsumeResult.Consumed).Should().Be(n,
            $"[{_backend.Name}] Limit == n*reserve, so every reservation must be admitted");

        var finalUsed = ctx.Quotas.First(q => q.Id == id).Used;
        finalUsed.Should().Be(n * actual,
            $"[{_backend.Name}] Used must reconcile to exactly n*actual after every reserve is settled — no lost updates");
        finalUsed.Should().BeGreaterThanOrEqualTo(0f,
            $"[{_backend.Name}] Used must never be driven negative by settlement");
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

using System.Linq.Expressions;

namespace Warp.Core.Data;

public interface IDataContext
{
    IQueryable<IUser> Users { get; }
    IQueryable<IApiKey> ApiKeys { get; }
    IQueryable<IRequest> Requests { get; }
    IQueryable<IQuota> Quotas { get; }
    Task SaveAsync<T>(T entity) where T : IEntity;
    Task UpsertAsync<T>(T entity, Expression<Func<T, bool>> filter) where T : IEntity;

    /// <summary>
    /// Atomically records <paramref name="amount"/> of usage against the quota identified by
    /// <paramref name="quotaId"/>. The read-check-write happens as a single atomic operation in
    /// the underlying store so concurrent callers cannot lose updates or overrun a prepaid limit.
    /// For prepaid quotas the increment is applied only when it keeps <c>Used &lt;= Limit</c>;
    /// postpaid quotas are always incremented.
    /// </summary>
    /// <param name="quotaId">The quota entity id.</param>
    /// <param name="amount">Units of usage to consume.</param>
    /// <returns>
    /// <see cref="QuotaConsumeResult.Consumed"/> when the usage was recorded,
    /// <see cref="QuotaConsumeResult.LimitExceeded"/> when a prepaid quota is exhausted, or
    /// <see cref="QuotaConsumeResult.NotFound"/> when no such quota exists.
    /// </returns>
    Task<QuotaConsumeResult> TryConsumeQuotaAsync(string quotaId, float amount);

    /// <summary>
    /// Atomically settles a previously reserved amount of usage against the quota identified by
    /// <paramref name="quotaId"/> by applying <paramref name="delta"/> to <c>Used</c> as a single
    /// indivisible operation (<c>Used = max(0, Used + delta)</c>).
    /// <para>
    /// This is used to reconcile a reservation taken up-front by <see cref="TryConsumeQuotaAsync"/>
    /// at admission time against the actual usage known only after the response:
    /// pass <c>actual - reserved</c> to charge/refund the difference, or <c>-reserved</c> to release
    /// the whole reservation when the request ultimately should not be billed. Unlike
    /// <see cref="TryConsumeQuotaAsync"/> the adjustment is unconditional (the request was already
    /// admitted and served), so it never fails on the prepaid limit; <c>Used</c> is floored at zero
    /// so an over-refund cannot drive the counter negative.
    /// </para>
    /// </summary>
    /// <param name="quotaId">The quota entity id.</param>
    /// <param name="delta">Signed adjustment to apply to <c>Used</c> (negative releases reservation).</param>
    Task SettleQuotaAsync(string quotaId, float delta);

    /// <summary>
    /// Atomically grants additional quota by adding <paramref name="amount"/> to the <c>Limit</c> of the
    /// quota identified by <paramref name="quotaId"/> as a single indivisible operation
    /// (<c>Limit = Limit + amount</c>). This closes the read-modify-write race on the credit-granting
    /// (money-in) path — concurrent grants cannot lose updates — mirroring the atomic guarantee
    /// <see cref="TryConsumeQuotaAsync"/> provides on the consume (money-out) side.
    /// </summary>
    /// <param name="quotaId">The quota entity id.</param>
    /// <param name="amount">Units of quota to add to the limit.</param>
    Task GrantQuotaAsync(string quotaId, float amount);

    /// <summary>
    /// Atomically evaluates and updates the token-bucket rate-limit state for <paramref name="key"/>.
    /// The refill calculation and the write are performed as a single atomic operation so
    /// concurrent requests for the same key cannot lose token decrements.
    /// </summary>
    /// <param name="key">Rate-limit bucket key (api key, user, or "anonymous").</param>
    /// <param name="rateLimitHz">Refill rate in tokens per second.</param>
    /// <param name="maxTokens">Maximum burst capacity of the bucket.</param>
    /// <param name="now">Current time used for refill math (typically <see cref="DateTime.UtcNow"/>).</param>
    /// <returns><c>true</c> when a token was available (request allowed); otherwise <c>false</c>.</returns>
    Task<bool> TryConsumeRateLimitAsync(string key, float rateLimitHz, float maxTokens, DateTime now);

    IUser CreateUser();
    IApiKey CreateApiKey();
    IRequest CreateRequest();
    IQuota CreateQuota();
}

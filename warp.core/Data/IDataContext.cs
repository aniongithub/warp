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

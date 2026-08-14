namespace Warp.Core.Data;

/// <summary>
/// Pure token-bucket math shared by every <see cref="IDataContext"/> implementation of
/// <see cref="IDataContext.TryConsumeRateLimitAsync"/>. Keeping the calculation in one place
/// avoids drift between backends while each backend supplies its own atomicity guarantee
/// (SQL transaction / row lock, Firestore transaction, or an in-process lock).
/// </summary>
public static class RateLimitTokenBucket
{
    /// <summary>
    /// Refills the bucket based on elapsed time and attempts to consume a single token.
    /// </summary>
    /// <param name="exists">Whether a persisted bucket already exists for the key.</param>
    /// <param name="lastUsed">Timestamp of the last recorded request (ignored when <paramref name="exists"/> is false).</param>
    /// <param name="lastRate">Token count recorded at <paramref name="lastUsed"/> (ignored when <paramref name="exists"/> is false).</param>
    /// <param name="now">Current time.</param>
    /// <param name="rateLimitHz">Refill rate in tokens per second.</param>
    /// <param name="maxTokens">Maximum burst capacity of the bucket.</param>
    /// <param name="remainingTokens">Token count to persist after a successful consume.</param>
    /// <returns><c>true</c> when a token was available (request allowed); otherwise <c>false</c>.</returns>
    public static bool TryConsume(
        bool exists,
        DateTime lastUsed,
        float lastRate,
        DateTime now,
        float rateLimitHz,
        float maxTokens,
        out float remainingTokens)
    {
        float tokens = maxTokens;
        if (exists)
        {
            var elapsed = (now - lastUsed).TotalSeconds;
            tokens = Math.Min(maxTokens, lastRate + (float)(elapsed * rateLimitHz));
        }

        if (tokens < 1)
        {
            remainingTokens = tokens;
            return false;
        }

        remainingTokens = tokens - 1; // consume one token
        return true;
    }
}

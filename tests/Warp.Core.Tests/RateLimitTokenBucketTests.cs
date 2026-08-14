using Warp.Core.Data;

namespace Warp.Core.Tests;

/// <summary>
/// Unit coverage for the pure token-bucket math shared by every IDataContext backend
/// (<see cref="RateLimitTokenBucket.TryConsume"/>). No storage, no time flakiness — time is injected.
/// </summary>
public class RateLimitTokenBucketTests
{
    private static readonly DateTime T0 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstRequest_StartsFull_AndConsumesOne()
    {
        var allowed = RateLimitTokenBucket.TryConsume(
            exists: false, lastUsed: default, lastRate: 0f,
            now: T0, rateLimitHz: 1f, maxTokens: 10f, out var remaining);

        allowed.Should().BeTrue();
        remaining.Should().Be(9f); // started at maxTokens (10), consumed one
    }

    [Fact]
    public void EmptyBucket_NoElapsedTime_IsRejected()
    {
        // lastRate 0 and now == lastUsed => no refill => 0 tokens => rejected.
        var allowed = RateLimitTokenBucket.TryConsume(
            exists: true, lastUsed: T0, lastRate: 0f,
            now: T0, rateLimitHz: 5f, maxTokens: 10f, out var remaining);

        allowed.Should().BeFalse();
        remaining.Should().Be(0f);
    }

    [Fact]
    public void Refill_AccruesTokensProportionalToElapsedSeconds()
    {
        // 2s elapsed at 3 tokens/s => +6 tokens on top of 0 => 6 available, consume one => 5.
        var allowed = RateLimitTokenBucket.TryConsume(
            exists: true, lastUsed: T0, lastRate: 0f,
            now: T0.AddSeconds(2), rateLimitHz: 3f, maxTokens: 100f, out var remaining);

        allowed.Should().BeTrue();
        remaining.Should().BeApproximately(5f, 1e-4f);
    }

    [Fact]
    public void Refill_IsCappedAtMaxTokens()
    {
        // Huge elapsed time cannot exceed the burst capacity.
        var allowed = RateLimitTokenBucket.TryConsume(
            exists: true, lastUsed: T0, lastRate: 4f,
            now: T0.AddHours(1), rateLimitHz: 1000f, maxTokens: 8f, out var remaining);

        allowed.Should().BeTrue();
        remaining.Should().Be(7f); // capped at 8 then consumed one
    }

    [Fact]
    public void PartialToken_BelowOne_IsRejected()
    {
        // 0.4s at 1 token/s starting from 0 => 0.4 tokens => below 1 => rejected, state preserved.
        var allowed = RateLimitTokenBucket.TryConsume(
            exists: true, lastUsed: T0, lastRate: 0f,
            now: T0.AddMilliseconds(400), rateLimitHz: 1f, maxTokens: 10f, out var remaining);

        allowed.Should().BeFalse();
        remaining.Should().BeApproximately(0.4f, 1e-3f);
    }

    [Fact]
    public void Sequential_Consumption_DrainsThenRejects()
    {
        // With no elapsed time between calls the bucket drains one token per call and then blocks.
        float rate = 0f;
        bool exists = false;
        var allowedCount = 0;
        for (var i = 0; i < 12; i++)
        {
            var ok = RateLimitTokenBucket.TryConsume(exists, T0, rate, T0, rateLimitHz: 1f, maxTokens: 5f, out var remaining);
            if (ok) allowedCount++;
            rate = remaining;
            exists = true;
        }

        // Started full at 5, so exactly 5 requests succeed before the bucket is empty.
        allowedCount.Should().Be(5);
    }
}

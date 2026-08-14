namespace Warp.Core.Job;

public class JobConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string ContextType { get; set; } = string.Empty;
    public string? DeliveryType { get; set; }
    public int MaxConcurrentJobs { get; set; } = 1;
    public int PollingIntervalMs { get; set; } = 5000;

    // At-least-once retry policy (bounded). A job is dispatched at most MaxAttempts times before it
    // is dead-lettered (moved to a terminal Failed state). Backoff between attempts grows
    // exponentially from RetryBackoffBaseSeconds and is capped at RetryBackoffMaxSeconds.
    public int MaxAttempts { get; set; } = 3;
    public double RetryBackoffBaseSeconds { get; set; } = 5;
    public double RetryBackoffMaxSeconds { get; set; } = 300;
}

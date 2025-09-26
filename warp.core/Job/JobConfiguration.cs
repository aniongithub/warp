namespace Warp.Core.Job;

public class JobConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string ContextType { get; set; } = string.Empty;
    public string? DeliveryType { get; set; }
    public int MaxConcurrentJobs { get; set; } = 1;
    public int PollingIntervalMs { get; set; } = 5000;
}

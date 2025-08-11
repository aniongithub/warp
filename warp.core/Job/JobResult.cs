namespace Warp.Core.Job;

public class JobResult
{
    public string JobId { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public DateTime QueuedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? Error { get; set; }
    public string? Output { get; set; }
}

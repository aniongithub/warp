using Warp.Core.Data;

namespace Warp.Core.Job;

public class Job : IJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public IUser? User { get; set; } = null;
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; } = null;
    public DateTime? EndedAt { get; set; } = null;
    public string? Error { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Warp internal routing data
    public string OriginalPath { get; set; } = string.Empty;
    public string ClusterId { get; set; } = string.Empty;
    public string TargetDestination { get; set; } = string.Empty;
    public Dictionary<string, object?> Parameters { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = new();

    // Actual API input/output data
    public string? Input { get; set; } = string.Empty;
    public string? Output { get; set; } = string.Empty;
}

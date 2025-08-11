using Warp.Core.Data;

namespace Warp.Core.Job;

public interface IJob : IEntity
{
    JobStatus Status { get; set; } // Current status of the job
    IUser? User { get; set; } // The user who submitted the job
    DateTime QueuedAt { get; set; } // When the job was queued
    DateTime? StartedAt { get; set; } // When the job started
    DateTime? EndedAt { get; set; } // When the job ended
    string? Error { get; set; } // Error message if failed

    // Warp internal routing data
    string OriginalPath { get; set; } // The original async API path
    string ClusterId { get; set; } // Target cluster ID
    string TargetDestination { get; set; } // Resolved destination URL
    Dictionary<string, object?> Parameters { get; set; } // Extracted API parameters
    Dictionary<string, string> Headers { get; set; } // Relevant headers

    // Actual API input/output data
    string? Input { get; set; } // Actual API request data
    string? Output { get; set; } // Actual API response data
}

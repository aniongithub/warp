using Warp.Core.Data;

namespace Warp.Core.Job;

public interface IJob : IEntity
{
    // Durable DAG identity. Standalone jobs use their own Id as RootJobId.
    string RootJobId { get; set; }
    // Jobs that must complete successfully before this job can enter the ready queue.
    List<string> DependsOn { get; set; }
    // Reverse edges materialized when a graph is enqueued, used to unlock/fail descendants.
    List<string> Dependents { get; set; }

    JobStatus Status { get; set; } // Current status of the job
    IUser? User { get; set; } // The user who submitted the job
    DateTime QueuedAt { get; set; } // When the job was queued
    DateTime? StartedAt { get; set; } // When the job started
    DateTime? EndedAt { get; set; } // When the job ended
    string? Error { get; set; } // Error message if failed
    int Attempts { get; set; } // Number of dispatch attempts already made (for bounded retries)

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

namespace Warp.Core.Data;

public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled
}

public interface IJob : IEntity
{
    JobStatus Status { get; set; } // Current status of the job
    IUser? User { get; set; } // The user who submitted the job
    DateTime QueuedAt { get; set; } // When the job was queued
    DateTime? StartedAt { get; set; } // When the job started
    DateTime? EndedAt { get; set; } // When the job ended
    string? Error { get; set; } // Error message if failed

    // TODO: Maybe this should only go in our message queue, not in the DB?
    string? Input { get; set; } // JSON or other serialized input data
    string? Output { get; set; } // JSON or other serialized output data
}
namespace Warp.Core.Job;

public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Canceled = 4,
    Waiting = 5
}

namespace Warp.Core.Job.Delivery;

/// <summary>
/// Interface for delivering job results to external systems (webhooks, push notifications, email, etc.)
/// </summary>
public interface IJobResultDelivery
{
    /// <summary>
    /// Deliver the job result when the job completes (success or failure)
    /// </summary>
    /// <param name="job">The completed job with all status, result, and error information</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeliverAsync(IJob job, CancellationToken cancellationToken = default);
}

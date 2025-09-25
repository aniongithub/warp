using Microsoft.Extensions.Logging;

namespace Warp.Core.Job.Delivery;

public abstract class ResultDeliveryBase<TOptions> : IJobResultDelivery where TOptions : class, new()
{
    protected string Name { get; }
    protected ILogger Logger { get; }
    protected TOptions Options { get; }

    protected ResultDeliveryBase(string name, ILogger logger, TOptions options)
    {
        Name = name;
        Logger = logger;
        Options = options;
    }

    public abstract Task DeliverAsync(IJob job, CancellationToken cancellationToken = default);
}

namespace Warp.Core.Job;

public interface IJobGraph
{
    IReadOnlyCollection<IJob> Jobs { get; }
    JobGraphNode Add();
}

namespace Warp.Core.Job;

public sealed class JobGraphNode
{
    internal JobGraphNode(JobGraph graph, IJob job)
    {
        Graph = graph;
        Job = job;
    }

    internal JobGraph Graph { get; }
    public IJob Job { get; }

    public JobGraphNode OnSuccess() => Graph.AddEdge(this, null, JobEdgeOutcome.Success);
    public JobGraphNode OnSuccess(JobGraphNode target) => Graph.AddEdge(this, target, JobEdgeOutcome.Success);
    public JobGraphNode OnFailure() => Graph.AddEdge(this, null, JobEdgeOutcome.Failure);
    public JobGraphNode OnFailure(JobGraphNode target) => Graph.AddEdge(this, target, JobEdgeOutcome.Failure);
    public JobGraphNode OnCompletion() => Graph.AddEdge(this, null, JobEdgeOutcome.Completion);
    public JobGraphNode OnCompletion(JobGraphNode target) => Graph.AddEdge(this, target, JobEdgeOutcome.Completion);
}

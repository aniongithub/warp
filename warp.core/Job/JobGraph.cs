namespace Warp.Core.Job;

public sealed class JobGraph : IJobGraph
{
    private readonly Func<IJob> _jobFactory;
    private readonly Dictionary<string, JobGraphNode> _nodes = new(StringComparer.Ordinal);

    internal JobGraph(Func<IJob> jobFactory)
    {
        _jobFactory = jobFactory ?? throw new ArgumentNullException(nameof(jobFactory));
    }

    public IReadOnlyCollection<IJob> Jobs => _nodes.Values.Select(node => node.Job).ToArray();

    public JobGraphNode Add()
    {
        var job = _jobFactory();
        if (job is null)
            throw new InvalidOperationException("The job factory returned null.");
        if (string.IsNullOrWhiteSpace(job.Id))
            job.Id = Guid.NewGuid().ToString();
        if (!_nodes.TryAdd(job.Id, new JobGraphNode(this, job)))
            throw new InvalidOperationException($"Job {job.Id} already belongs to this graph.");
        return _nodes[job.Id];
    }

    internal JobGraphNode AddEdge(JobGraphNode source, JobGraphNode? target, JobEdgeOutcome outcome)
    {
        EnsureMember(source);
        target ??= Add();
        EnsureMember(target);
        if (ReferenceEquals(source, target))
            throw new ArgumentException("A job cannot target itself.", nameof(target));

        var edges = outcome switch
        {
            JobEdgeOutcome.Success => source.Job.OnSuccess,
            JobEdgeOutcome.Failure => source.Job.OnFailure,
            JobEdgeOutcome.Completion => source.Job.OnCompletion,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        if (!edges.Contains(target.Job.Id, StringComparer.Ordinal))
            edges.Add(target.Job.Id);
        return target;
    }

    private void EnsureMember(JobGraphNode node)
    {
        if (!ReferenceEquals(node.Graph, this) || !_nodes.ContainsKey(node.Job.Id))
            throw new ArgumentException("Both jobs must belong to the same graph.", nameof(node));
    }
}

internal enum JobEdgeOutcome
{
    Success,
    Failure,
    Completion
}

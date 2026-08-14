namespace Warp.Integration.Tests.Infrastructure;

/// <summary>
/// Launches <paramref name="count"/> operations that all block on a single gate and are then
/// released simultaneously, maximising real contention on the shared backend. Returns the
/// results in completion-independent (launch) order.
/// </summary>
public static class ParallelRunner
{
    public static async Task<T[]> RunAllAsync<T>(int count, Func<int, Task<T>> operation)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, count)
            .Select(async i =>
            {
                await gate.Task;
                return await operation(i);
            })
            .ToArray();

        // Release every task at once.
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }
}

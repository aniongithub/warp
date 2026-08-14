using Microsoft.AspNetCore.Http;
using Warp.Core.Middleware;

namespace Warp.Core.Tests;

/// <summary>
/// Coverage for the pipeline-control wrapper (<see cref="Result"/>) and the
/// <c>.Continue()</c> / <c>.Stop()</c> extensions: only the Stop path executes the inner result and
/// short-circuits; the Continue path leaves the inner result untouched so the pipeline proceeds.
/// </summary>
public class ResultPipelineActionTests
{
    private sealed class SpyResult : IResult
    {
        public int ExecutionCount { get; private set; }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            ExecutionCount++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Continue_SetsContinueAction_AndWrapsInner()
    {
        var inner = new SpyResult();
        var wrapped = inner.Continue();

        wrapped.Action.Should().Be(PipelineAction.Continue);
        wrapped.InnerResult.Should().BeSameAs(inner);
    }

    [Fact]
    public void Stop_SetsStopAction_AndWrapsInner()
    {
        var inner = new SpyResult();
        var wrapped = inner.Stop();

        wrapped.Action.Should().Be(PipelineAction.Stop);
        wrapped.InnerResult.Should().BeSameAs(inner);
    }

    [Fact]
    public async Task Stop_Executes_InnerResult()
    {
        var inner = new SpyResult();
        var wrapped = inner.Stop();

        await wrapped.ExecuteAsync(new DefaultHttpContext());

        inner.ExecutionCount.Should().Be(1, "the Stop path must execute the wrapped result");
    }

    [Fact]
    public async Task Continue_Does_Not_Execute_InnerResult()
    {
        var inner = new SpyResult();
        var wrapped = inner.Continue();

        await wrapped.ExecuteAsync(new DefaultHttpContext());

        inner.ExecutionCount.Should().Be(0, "the Continue path must NOT execute the wrapped result");
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Warp.Core.Middleware;

namespace Warp.Core.Tests;

/// <summary>
/// Exposes the protected static <c>GetOperationType</c> so the URL-shape → operation matrix can be
/// asserted directly. The class is abstract and never instantiated; only the inherited static logic
/// is exercised.
/// </summary>
internal abstract class OperationTypeProbe : MiddlewareBase<MiddlewareOptions>
{
    private OperationTypeProbe() : base("probe", NullLogger.Instance, null!, new MiddlewareOptions()) { }

    public static string Resolve(string path, string method) => GetOperationType(path, method);
}

/// <summary>
/// Exhaustive coverage of <c>MiddlewareBase.GetOperationType</c>: the URL shape and HTTP method that
/// classify a request as Sync / AsyncSubmit / AsyncStatus / AsyncResult / AsyncCancel, including the
/// "submit must be the last segment" quirk and method sensitivity.
/// </summary>
public class GetOperationTypeTests
{
    [Theory]
    // Empty / root path is always Sync.
    [InlineData("", "GET", "Sync")]
    [InlineData("/", "POST", "Sync")]
    // submit must be the LAST segment, POST or GET.
    [InlineData("/api/echo/submit", "POST", "AsyncSubmit")]
    [InlineData("/api/echo/submit", "GET", "AsyncSubmit")]
    [InlineData("/submit", "POST", "AsyncSubmit")]
    // submit present but NOT the last segment => Sync.
    [InlineData("/api/submit/extra", "POST", "Sync")]
    // submit is last but wrong method => Sync (method sensitivity).
    [InlineData("/api/echo/submit", "PUT", "Sync")]
    [InlineData("/api/echo/submit", "DELETE", "Sync")]
    // status: keyword must be second-to-last (jobId last), GET only.
    [InlineData("/api/echo/status/job-123", "GET", "AsyncStatus")]
    [InlineData("/api/echo/status/job-123", "POST", "Sync")]
    // status as the very last segment (no jobId) => Sync.
    [InlineData("/api/echo/status", "GET", "Sync")]
    // result: second-to-last, GET only.
    [InlineData("/api/echo/result/job-123", "GET", "AsyncResult")]
    [InlineData("/api/echo/result/job-123", "DELETE", "Sync")]
    // cancel: second-to-last, DELETE only.
    [InlineData("/api/echo/cancel/job-123", "DELETE", "AsyncCancel")]
    [InlineData("/api/echo/cancel/job-123", "GET", "Sync")]
    // A plain sync request with none of the keywords.
    [InlineData("/api/echo/run", "POST", "Sync")]
    public void Classifies_UrlShape_And_Method(string path, string method, string expected)
    {
        OperationTypeProbe.Resolve(path, method).Should().Be(expected);
    }

    [Theory]
    // Keyword and method matching are case-insensitive.
    [InlineData("/api/echo/SUBMIT", "post", "AsyncSubmit")]
    [InlineData("/api/echo/Status/job-1", "get", "AsyncStatus")]
    [InlineData("/api/echo/CANCEL/job-1", "delete", "AsyncCancel")]
    public void Is_CaseInsensitive(string path, string method, string expected)
    {
        OperationTypeProbe.Resolve(path, method).Should().Be(expected);
    }

    [Fact]
    public void Submit_Is_Checked_Before_Status_When_Both_Match()
    {
        // "/status/submit" GET: submit is the last segment (AsyncSubmit) AND status is second-to-last.
        // submit is evaluated first, so the result must be AsyncSubmit.
        OperationTypeProbe.Resolve("/status/submit", "GET").Should().Be("AsyncSubmit");
    }
}

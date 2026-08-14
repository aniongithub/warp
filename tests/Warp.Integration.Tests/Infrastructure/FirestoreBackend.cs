using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Warp.Core.Data;
using Warp.Core.Data.Contexts;

namespace Warp.Integration.Tests.Infrastructure;

/// <summary>
/// Firestore backend backed by the gcloud Firestore emulator (no dedicated Testcontainers
/// module exists, so a generic container is used).
///
/// CAVEAT: the emulator validates the transaction CODE PATH (RunTransactionAsync retries on
/// contention) but is NOT a perfect model of production Firestore contention timing. A green
/// run here proves the atomic logic is correct, not that prod latency/contention is identical.
/// </summary>
public sealed class FirestoreBackend : IDataContextBackend
{
    private const string ProjectId = "warp-test";

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("gcr.io/google.com/cloudsdktool/google-cloud-cli:emulators")
        .WithCommand(
            "gcloud", "emulators", "firestore", "start",
            "--host-port=0.0.0.0:8080", $"--project={ProjectId}")
        .WithPortBinding(8080, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilMessageIsLogged("Dev App Server is now running"))
        .Build();

    public string Name => "Firestore";
    // Total operations issued. The emulator cannot serialize many *simultaneous* single-document
    // transactions (it aborts with "Transaction lock timeout" and, unlike production Firestore,
    // livelocks under sustained retry), so instantaneous concurrency is capped via _gate below while
    // still driving a meaningful volume of overlapping transactions through the atomic code path.
    public int Parallelism => 40;
    public IDataContext Context { get; private set; } = null!;

    // Caps how many Firestore transactions contend on the same document at once. A handful of
    // genuinely-overlapping transactions is enough to exercise the read-check-write / retry logic;
    // beyond that the emulator (not the product) becomes the bottleneck.
    private readonly SemaphoreSlim _gate = new(4, 4);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var host = $"{_container.Hostname}:{_container.GetMappedPublicPort(8080)}";
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", host);
        Context = new FirestoreDataContext(ProjectId);
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", null);
        _gate.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Runs a Firestore operation under a concurrency cap plus <c>Aborted</c>/lock-timeout retry.
    /// Production Firestore (and its client) retry aborted transactions with backoff; the emulator
    /// needs a larger budget and cannot sustain high simultaneous single-doc contention, so we bound
    /// concurrency here. This never hides a correctness failure — the no-lost-update / no-overrun
    /// invariants are asserted only after every operation has ultimately committed.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        await _gate.WaitAsync();
        try
        {
            const int maxAttempts = 60;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await operation();
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransientContention(ex))
                {
                    // Bounded, jittered backoff to let the contending transactions drain.
                    var delayMs = Math.Min(200, 10 * attempt) + Random.Shared.Next(0, 15);
                    await Task.Delay(delayMs);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsTransientContention(Exception ex)
    {
        // Matched structurally (by type name + message) to avoid a direct Grpc.Core package
        // reference in the test project.
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e.GetType().Name == "RpcException" &&
                (e.Message.Contains("Aborted", StringComparison.OrdinalIgnoreCase) ||
                 e.Message.Contains("lock timeout", StringComparison.OrdinalIgnoreCase) ||
                 e.Message.Contains("contention", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }
}

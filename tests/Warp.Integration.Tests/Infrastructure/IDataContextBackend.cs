using Warp.Core.Data;

namespace Warp.Integration.Tests.Infrastructure;

/// <summary>
/// A backend under test for the atomicity suite. Each implementation stands up one shared
/// <see cref="IDataContext"/> (modelling the production DI singleton) against a real backing
/// store — a temp file for Json/Sqlite, a Testcontainers container for PostgreSql/Firestore.
///
/// Adding a backend is a one-line change: implement this interface and derive a test class
/// from <see cref="AtomicityTestsBase{TBackend}"/>.
/// </summary>
public interface IDataContextBackend : IAsyncLifetime
{
    /// <summary>Human-readable backend name used in assertion messages.</summary>
    string Name { get; }

    /// <summary>The shared context all parallel operations run against.</summary>
    IDataContext Context { get; }

    /// <summary>How many concurrent operations the atomicity tests launch for this backend.</summary>
    int Parallelism { get; }

    /// <summary>
    /// Runs a single atomicity operation, giving the backend a chance to apply backend-specific
    /// transient-fault handling. Most backends run the operation directly; the Firestore emulator
    /// overrides this to retry <c>Aborted</c>/lock-timeout aborts with backoff, exactly as the
    /// production Firestore client does. This never masks a lost update or an overrun — those are
    /// asserted after every operation has completed.
    /// </summary>
    Task<T> ExecuteAsync<T>(Func<Task<T>> operation);
}

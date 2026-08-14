# Warp tests

Automated test suite for the Warp API gateway. This is the foundation every future test builds
on — please keep the conventions below.

## Layout

| Project | Kind | What it covers |
| --- | --- | --- |
| `Warp.Core.Tests` | Unit (fast, no containers) | `ConfigurationExtensions` (`$include` / `${VAR}` resolution), `MiddlewareBase.GetOperationType`, `Result`/`PipelineAction`, `RateLimitTokenBucket` token math |
| `Warp.Dilithium.Tests` | Unit | `JwtValidator` decisions (signed / unsigned / expired / fail-closed) and JWKS cache hit + stale-serve |
| `Warp.Latinum.Tests` | Unit | Stripe webhook signature verification (`EventUtility.ConstructEvent`, pure crypto) |
| `Warp.Integration.Tests` | Integration (Testcontainers) | Backend-parametrized atomicity/concurrency suite + `RedisJobContext` reliable-queue behavior |

Shared tooling versions and conventions live in [`Directory.Build.props`](./Directory.Build.props):
net9.0, nullable + implicit usings, xUnit + FluentAssertions, `Xunit` and `FluentAssertions`
imported globally. Individual `.csproj` files only add the project-under-test reference and any
backend-specific packages (Testcontainers, Stripe.net, ...).

## Naming

- One test project per production project, named `<Project>.Tests`.
- Test method names read as a sentence describing the guaranteed behavior, e.g.
  `Prepaid_quota_consume_is_exact_under_parallelism`.
- Unit tests must not touch the network, the clock (beyond `DateTime.UtcNow` passed as data), or
  containers.

## Running

All local builds/tests run **inside the dev container** (see `.devcontainer/`). From the repo root:

```bash
# everything
dotnet test warp.sln -c Debug

# a single project
dotnet test tests/Warp.Core.Tests/Warp.Core.Tests.csproj -c Debug

# a single backend of the atomicity suite
dotnet test tests/Warp.Integration.Tests/Warp.Integration.Tests.csproj \
  --filter FullyQualifiedName~PostgreSqlAtomicityTests
```

The integration tests need a Docker daemon (Testcontainers starts Postgres, Redis and the Firestore
emulator automatically). No manual container setup is required.

> The devcontainer-only rule is a **local** host-protection guard. CI runners are ephemeral Linux
> boxes with Docker, so CI runs `dotnet`/Testcontainers directly on the runner — see
> [`.github/workflows/ci.yml`](../.github/workflows/ci.yml).

## The atomicity suite (highest-value regression guard)

[`AtomicityTests.cs`](./Warp.Integration.Tests/AtomicityTests.cs) runs the **same** assertions
against **every** `IDataContext` backend so the atomicity hardening is guarded identically
everywhere:

- **Prepaid quota consume** — N parallel `TryConsumeQuotaAsync` against a limit `< N`; asserts the
  final `Used` is exactly the limit (no lost updates), never overruns, and `LimitExceeded` is
  returned exactly the right number of times.
- **Rate limit** — N parallel `TryConsumeRateLimitAsync` at a fixed instant; asserts the number of
  allowed requests never exceeds the bucket capacity.

### Adding a backend is one line

1. Implement [`IDataContextBackend`](./Warp.Integration.Tests/Infrastructure/IDataContextBackend.cs)
   (stand up the store, expose a shared `IDataContext`, pick a `Parallelism`). Container-backed
   backends do their setup in `InitializeAsync`.
2. Add a one-line derived class at the bottom of `AtomicityTests.cs`:

   ```csharp
   public sealed class MyBackendAtomicityTests : AtomicityTestsBase<MyBackend>
   {
       public MyBackendAtomicityTests(MyBackend backend) : base(backend) { }
   }
   ```

Current backends: `JsonBackend`, `SqliteBackend` (temp files, 200-way parallel),
`PostgreSqlBackend` (Testcontainers, 200-way parallel), `FirestoreBackend` (emulator).

### Firestore emulator caveat

The Firestore emulator validates the transaction **code path** (`RunTransactionAsync` read-check-write
+ retry) but is **not** a faithful model of production Firestore contention timing: it uses
short-timeout pessimistic locks and livelocks under many simultaneous single-document transactions.
The `FirestoreBackend` therefore caps instantaneous concurrency and retries `Aborted`/lock-timeout
aborts (exactly as the production Firestore client does). A green run proves the atomic logic is
correct — do not read it as a production contention benchmark.

## Not yet covered (follow-ups)

- **Grant / Settle atomicity** (PR #32): once `GrantQuotaAsync` / `SettleQuotaAsync` land on
  `IDataContext`, extend `AtomicityTestsBase` with the parallel-grant and interleaved reserve/settle
  cases (a `TODO(#32)` marks the spot).
- **Stripe/localstripe end-to-end** and **full-stack e2e** layers — deliberately out of scope for
  this foundation; the solution is structured so they slot in as additional `tests/` projects.

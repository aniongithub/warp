using Microsoft.Extensions.Configuration;
using Warp.Core.Data;
using Warp.Core.Data.Contexts;
using Warp.Core.Journal;

namespace Warp.Core.Tests;

public class JournalTests
{
    [Theory]
    [InlineData("jsonl")]
    [InlineData("sqlite")]
    public async Task Async_disposable_transaction_records_fluent_typed_and_nested_entries(string backend)
    {
        using var environment = new JournalTestEnvironment(backend);
        var store = environment.CreateStore();
        var user = new SqliteDataContext.User { Id = "user-1", Email = "user@example.test" };
        var apiKey = new SqliteDataContext.ApiKey { Id = "key-1", Key = "secret-value", Owner = user.Id };
        var request = new SqliteDataContext.Request { Id = "request-1", Key = "request-key" };
        var quota = new SqliteDataContext.Quota { Id = "quota-1", Key = user.Id, QuotaName = "tokens" };

        string rootId;
        await using (var transaction = await store.BeginAsync("tests", request, user, apiKey, "request"))
        {
            rootId = transaction.Root.Id;
            var fact = transaction.Root
                .CreateEntry("quota.consume", request, quota)
                .WithData(new { amount = 4, unit = "tokens" })
                .WithSchemaVersion(2);
            var measurement = fact
                .CreateChild("quota.consume.measurement", request)
                .WithData(new { measured = 4 })
                .WithCause(fact);

            await transaction.RecordAsync([fact, measurement]);
            await transaction.CommitAsync();
        }

        var entries = await ReadAllAsync(store, new JournalQuery(RootId: rootId));
        entries.Select(entry => entry.Sequence).Should().Equal(0, 1, 2, 3);
        entries.Select(entry => entry.Phase).Should().Equal(
            JournalPhase.Started,
            JournalPhase.Fact,
            JournalPhase.Fact,
            JournalPhase.Completed);
        entries[1].SubjectType.Should().Be(typeof(IRequest).FullName);
        entries[1].SubjectId.Should().Be(request.Id);
        entries[1].ObjectType.Should().Be(typeof(IQuota).FullName);
        entries[1].ObjectId.Should().Be(quota.Id);
        entries[1].UserId.Should().Be(user.Id);
        entries[1].ApiKeyId.Should().Be(apiKey.Id);
        entries[1].SchemaVersion.Should().Be(2);
        entries[1].Data.GetProperty("amount").GetInt32().Should().Be(4);
        entries[2].ParentId.Should().Be(entries[1].Id);
        entries[2].CausedById.Should().Be(entries[1].Id);
        entries[2].RootId.Should().Be(rootId);
        entries[3].OutcomeCode.Should().Be("completed");
    }

    [Theory]
    [InlineData("jsonl")]
    [InlineData("sqlite")]
    public async Task Disposing_active_scope_records_indeterminate_outcome(string backend)
    {
        using var environment = new JournalTestEnvironment(backend);
        var store = environment.CreateStore();
        var transaction = await store.BeginAsync("tests");
        var rootId = transaction.Root.Id;

        await transaction.DisposeAsync();

        var entries = await ReadAllAsync(store, new JournalQuery(RootId: rootId));
        entries.Select(entry => entry.Phase).Should().Equal(
            JournalPhase.Started,
            JournalPhase.Indeterminate);
    }

    [Theory]
    [InlineData("jsonl")]
    [InlineData("sqlite")]
    public async Task Cancellation_is_recorded_even_when_operation_token_is_already_cancelled(string backend)
    {
        using var environment = new JournalTestEnvironment(backend);
        var store = environment.CreateStore();
        await using var transaction = await store.BeginAsync("tests");
        var rootId = transaction.Root.Id;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await transaction.CancelAsync(cancellation.Token);

        var entries = await ReadAllAsync(store, new JournalQuery(RootId: rootId));
        entries[^1].Phase.Should().Be(JournalPhase.Cancelled);
    }

    [Theory]
    [InlineData("jsonl")]
    [InlineData("sqlite")]
    public async Task Append_is_idempotent_and_rejects_conflicting_reuse(string backend)
    {
        using var environment = new JournalTestEnvironment(backend);
        var store = environment.CreateStore();
        var root = CreateRoot("root-idempotent");
        var record = JournalRecord.FromEntry(root);
        var append = new JournalAppend(root.Id, -1, "stable-key", [record]);

        var first = await store.AppendAsync(append);
        var second = await store.AppendAsync(append);

        first.Should().Be(new JournalAppendResult(0, false));
        second.Should().Be(new JournalAppendResult(0, true));

        var changed = record with { Kind = "changed" };
        var action = () => store.AppendAsync(new JournalAppend(root.Id, -1, "stable-key", [changed]));
        await action.Should().ThrowAsync<JournalIdempotencyException>();
    }

    [Theory]
    [InlineData("jsonl")]
    [InlineData("sqlite")]
    public async Task Store_rejects_stale_sequence_and_second_terminal_outcome(string backend)
    {
        using var environment = new JournalTestEnvironment(backend);
        var store = environment.CreateStore();
        var root = CreateRoot("root-terminal");
        await store.AppendAsync(new JournalAppend(root.Id, -1, "root", [JournalRecord.FromEntry(root)]));

        var staleFact = CreateChild(root, "fact", JournalPhase.Fact, 2);
        var stale = () => store.AppendAsync(new JournalAppend(root.Id, 1, "stale", [JournalRecord.FromEntry(staleFact)]));
        await stale.Should().ThrowAsync<JournalConcurrencyException>();

        var completed = CreateChild(root, "completed", JournalPhase.Completed, 1);
        await store.AppendAsync(new JournalAppend(root.Id, 0, "completed", [JournalRecord.FromEntry(completed)]));

        var failed = CreateChild(root, "failed", JournalPhase.Failed, 2);
        var secondTerminal = () => store.AppendAsync(
            new JournalAppend(root.Id, 1, "failed", [JournalRecord.FromEntry(failed)]));
        await secondTerminal.Should().ThrowAsync<JournalTerminalStateException>();
    }

    [Theory]
    [InlineData("jsonl")]
    [InlineData("sqlite")]
    public async Task Concurrent_store_instances_allow_only_one_append_at_an_expected_sequence(string backend)
    {
        using var environment = new JournalTestEnvironment(backend);
        var firstStore = environment.CreateStore();
        var secondStore = environment.CreateStore();
        var root = CreateRoot("root-concurrent");
        await firstStore.AppendAsync(new JournalAppend(root.Id, -1, "root", [JournalRecord.FromEntry(root)]));
        var firstFact = CreateChild(root, "fact-1", JournalPhase.Fact, 1);
        var secondFact = CreateChild(root, "fact-2", JournalPhase.Fact, 1);

        var attempts = await Task.WhenAll(
            CaptureAsync(() => firstStore.AppendAsync(
                new JournalAppend(root.Id, 0, "fact-1", [JournalRecord.FromEntry(firstFact)]))),
            CaptureAsync(() => secondStore.AppendAsync(
                new JournalAppend(root.Id, 0, "fact-2", [JournalRecord.FromEntry(secondFact)]))));

        attempts.Count(exception => exception is null).Should().Be(1);
        attempts.Count(exception => exception is JournalConcurrencyException).Should().Be(1);
    }

    [Theory]
    [InlineData("jsonl")]
    [InlineData("sqlite")]
    public async Task Durable_store_reopens_with_existing_entries(string backend)
    {
        using var environment = new JournalTestEnvironment(backend);
        var store = environment.CreateStore();
        string rootId;
        await using (var transaction = await store.BeginAsync("tests"))
        {
            rootId = transaction.Root.Id;
            await transaction.CommitAsync();
        }

        var reopened = environment.CreateStore();
        var entries = await ReadAllAsync(reopened, new JournalQuery(RootId: rootId));
        entries.Select(entry => entry.Phase).Should().Equal(
            JournalPhase.Started,
            JournalPhase.Completed);
    }

    [Fact]
    public async Task Jsonl_recovery_truncates_incomplete_trailing_batch()
    {
        using var environment = new JournalTestEnvironment("jsonl");
        var store = environment.CreateStore();
        var root = CreateRoot("root-recovery");
        await store.AppendAsync(new JournalAppend(root.Id, -1, "root", [JournalRecord.FromEntry(root)]));
        var validLength = new FileInfo(environment.Path).Length;
        await File.AppendAllTextAsync(environment.Path, "{\"incomplete\":");

        var reopened = environment.CreateStore();
        var entries = await ReadAllAsync(reopened, new JournalQuery(RootId: root.Id));

        entries.Should().ContainSingle();
        new FileInfo(environment.Path).Length.Should().Be(validLength);
    }

    [Fact]
    public void Configuration_factory_creates_configured_store()
    {
        using var environment = new JournalTestEnvironment("sqlite");
        var values = new Dictionary<string, string?>
        {
            ["Journal:Type"] = typeof(SqliteJournalStore).AssemblyQualifiedName,
            ["Journal:Args:filePath"] = environment.Path
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var store = configuration.GetSection("Journal").CreateJournalStore();

        store.Should().BeOfType<SqliteJournalStore>();
    }

    private static JournalEntry CreateRoot(string id)
    {
        var root = new JournalEntry
        {
            Id = id,
            Kind = "test",
            Phase = JournalPhase.Started,
            Sequence = 0,
            Component = "tests"
        };
        root.Root = root;
        return root;
    }

    private static JournalEntry CreateChild(
        IJournalEntry root,
        string id,
        JournalPhase phase,
        long sequence) => new()
    {
        Id = id,
        Root = root,
        Parent = root,
        Kind = $"test.{phase.ToString().ToLowerInvariant()}",
        Phase = phase,
        Sequence = sequence,
        Component = "tests"
    };

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<List<JournalRecord>> ReadAllAsync(
        IJournalStore store,
        JournalQuery query)
    {
        var entries = new List<JournalRecord>();
        await foreach (var entry in store.ReadAsync(query))
            entries.Add(entry);
        return entries;
    }

    private sealed class JournalTestEnvironment : IDisposable
    {
        private readonly string _directory;
        private readonly string _backend;

        internal JournalTestEnvironment(string backend)
        {
            _backend = backend;
            _directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"warp-journal-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(
                _directory,
                backend == "sqlite" ? "journal.db" : "journal.jsonl");
        }

        internal string Path { get; }

        internal IJournalStore CreateStore() => _backend switch
        {
            "jsonl" => new JsonLinesJournalStore(Path),
            "sqlite" => new SqliteJournalStore(Path),
            _ => throw new ArgumentOutOfRangeException(nameof(_backend), _backend, "Unknown test backend.")
        };

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // SQLite may briefly retain a WAL handle on slower CI hosts.
            }
        }
    }
}

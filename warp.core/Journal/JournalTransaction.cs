using System.Text.Json;
using Warp.Core.Data;

namespace Warp.Core.Journal;

public interface IJournalTransaction : IAsyncDisposable
{
    IJournalEntry Root { get; }

    Task RecordAsync(IJournalEntry entry, CancellationToken cancellationToken = default);
    Task RecordAsync(IReadOnlyCollection<IJournalEntry> entries, CancellationToken cancellationToken = default);
    Task CommitAsync(CancellationToken cancellationToken = default);
    Task FailAsync(Exception exception, CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
}

public static class JournalStoreTransactionExtensions
{
    public static async Task<IJournalTransaction> BeginAsync(
        this IJournalStore store,
        string component,
        IRequest? request = null,
        IUser? user = null,
        IApiKey? apiKey = null,
        string kind = "transaction",
        JsonElement data = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(component))
            throw new ArgumentException("Component cannot be empty.", nameof(component));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Root kind cannot be empty.", nameof(kind));

        var root = new JournalEntry
        {
            Kind = kind,
            Phase = JournalPhase.Started,
            Sequence = 0,
            Component = component,
            Request = request,
            User = user,
            ApiKey = apiKey,
            Data = data.ValueKind == JsonValueKind.Undefined ? JournalJson.EmptyObject() : data.Clone()
        };
        root.Root = root;

        var append = new JournalAppend(
            root.Id,
            -1,
            $"{root.Id}:started",
            [JournalRecord.FromEntry(root)]);
        await store.AppendAsync(append, cancellationToken);
        return new JournalTransaction(store, root);
    }

    public static async Task<T> ExecuteAsync<T>(
        this IJournalStore store,
        string component,
        Func<IJournalTransaction, CancellationToken, Task<T>> operation,
        IRequest? request = null,
        IUser? user = null,
        IApiKey? apiKey = null,
        string kind = "transaction",
        JsonElement data = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(operation);
        await using var transaction = await store.BeginAsync(
            component,
            request,
            user,
            apiKey,
            kind,
            data,
            cancellationToken);

        try
        {
            var result = await operation(transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (OperationCanceledException)
        {
            await transaction.CancelAsync(cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            await transaction.FailAsync(exception, cancellationToken);
            throw;
        }
    }
}

internal sealed class JournalTransaction(IJournalStore store, JournalEntry root) : IJournalTransaction
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly IJournalStore _store = store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _sequence;
    private JournalPhase? _terminalPhase;
    private string? _terminalEntryId;
    private string? _terminalOutcomeCode;
    private string? _terminalOutcomeMessage;
    private bool? _terminalRetryable;
    private bool _completed;

    public IJournalEntry Root { get; } = root;

    public Task RecordAsync(IJournalEntry entry, CancellationToken cancellationToken = default) =>
        RecordAsync([entry], cancellationToken);

    public async Task RecordAsync(
        IReadOnlyCollection<IJournalEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            throw new ArgumentException("At least one entry is required.", nameof(entries));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureActive();
            var records = new List<JournalRecord>(entries.Count);
            var nextSequence = _sequence;
            foreach (var entry in entries)
            {
                if (entry.Phase != JournalPhase.Fact)
                    throw new ArgumentException("RecordAsync accepts fact entries only.", nameof(entries));

                PrepareEntry(entry, ++nextSequence);
                records.Add(JournalRecord.FromEntry(entry));
            }

            var append = new JournalAppend(
                Root.Id,
                _sequence,
                $"{Root.Id}:facts:{string.Join(':', records.Select(record => record.Id))}",
                records);
            var result = await _store.AppendAsync(append, cancellationToken);
            _sequence = result.LastSequence;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        AppendTerminalAsync(JournalPhase.Completed, "completed", null, false, cancellationToken);

    public Task FailAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return AppendTerminalAsync(
            JournalPhase.Failed,
            exception.GetType().FullName ?? exception.GetType().Name,
            null,
            null,
            cancellationToken);
    }

    public Task CancelAsync(CancellationToken cancellationToken = default) =>
        AppendTerminalAsync(JournalPhase.Cancelled, "cancelled", null, true, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_completed)
        {
            _gate.Dispose();
            return;
        }

        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        if (_terminalPhase.HasValue)
        {
            await AppendTerminalAsync(
                _terminalPhase.Value,
                _terminalOutcomeCode!,
                _terminalOutcomeMessage,
                _terminalRetryable,
                cleanup.Token);
        }
        else
        {
            await AppendTerminalAsync(
                JournalPhase.Indeterminate,
                "scope-disposed-without-terminal-outcome",
                null,
                true,
                cleanup.Token);
        }
        _gate.Dispose();
    }

    private async Task AppendTerminalAsync(
        JournalPhase phase,
        string outcomeCode,
        string? outcomeMessage,
        bool? retryable,
        CancellationToken cancellationToken)
    {
        using var cleanup = CreateTerminalToken(cancellationToken);
        await _gate.WaitAsync(cleanup.Token);
        try
        {
            if (_completed)
            {
                if (_terminalPhase == phase)
                    return;
                throw new JournalTerminalStateException(
                    $"Transaction {Root.Id} already ended as {_terminalPhase}.");
            }

            if (_terminalPhase.HasValue && _terminalPhase != phase)
                throw new JournalTerminalStateException(
                    $"Transaction {Root.Id} already has a pending {_terminalPhase} terminal write.");

            if (!_terminalPhase.HasValue)
            {
                _terminalPhase = phase;
                _terminalOutcomeCode = outcomeCode;
                _terminalOutcomeMessage = outcomeMessage;
                _terminalRetryable = retryable;
            }
            _terminalEntryId ??= Guid.NewGuid().ToString("N");
            var terminal = new JournalEntry
            {
                Id = _terminalEntryId,
                Root = Root,
                Parent = Root,
                Request = Root.Request,
                User = Root.User,
                ApiKey = Root.ApiKey,
                Kind = $"{Root.Kind}.{phase.ToString().ToLowerInvariant()}",
                Phase = phase,
                Sequence = _sequence + 1,
                Component = Root.Component,
                OutcomeCode = _terminalOutcomeCode,
                OutcomeMessage = _terminalOutcomeMessage,
                Retryable = _terminalRetryable
            };

            var append = new JournalAppend(
                Root.Id,
                _sequence,
                $"{Root.Id}:terminal:{_terminalEntryId}",
                [JournalRecord.FromEntry(terminal)]);
            var result = await _store.AppendAsync(append, cleanup.Token);
            _sequence = result.LastSequence;
            _completed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PrepareEntry(IJournalEntry entry, long sequence)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
            entry.Id = Guid.NewGuid().ToString("N");
        entry.Root = Root;
        entry.Parent ??= Root;
        entry.Sequence = sequence;
        entry.Request ??= Root.Request;
        entry.User ??= Root.User;
        entry.ApiKey ??= Root.ApiKey;
        if (entry.Timestamp == default)
            entry.Timestamp = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(entry.Component))
            entry.Component = Root.Component;
        if (string.IsNullOrWhiteSpace(entry.Kind))
            throw new ArgumentException("Journal entry kind cannot be empty.", nameof(entry));
    }

    private void EnsureActive()
    {
        if (_completed || _terminalPhase.HasValue)
            throw new JournalTerminalStateException($"Transaction {Root.Id} is no longer active.");
    }

    private static CancellationTokenSource CreateTerminalToken(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new CancellationTokenSource(CleanupTimeout);

        var cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cleanup.CancelAfter(CleanupTimeout);
        return cleanup;
    }
}

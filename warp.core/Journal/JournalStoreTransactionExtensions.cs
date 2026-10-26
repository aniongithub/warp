using System.Text.Json;
using Warp.Core.Data;

namespace Warp.Core.Journal;

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

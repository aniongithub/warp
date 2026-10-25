using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Warp.Core.Data;
using Warp.Core.Job;

namespace Warp.Core.Journal;

public enum JournalPhase
{
    Started,
    Fact,
    Completed,
    Failed,
    Cancelled,
    Indeterminate
}

public interface IJournalEntry : IEntity
{
    IJournalEntry Root { get; set; }
    IJournalEntry? Parent { get; set; }
    IJournalEntry? CausedBy { get; set; }
    long Sequence { get; set; }

    IRequest? Request { get; set; }
    IUser? User { get; set; }
    IApiKey? ApiKey { get; set; }

    string Kind { get; set; }
    int SchemaVersion { get; set; }
    JournalPhase Phase { get; set; }
    DateTimeOffset Timestamp { get; set; }
    string Component { get; set; }
    JsonElement Data { get; set; }

    string? OutcomeCode { get; set; }
    string? OutcomeMessage { get; set; }
    bool? Retryable { get; set; }
}

public interface IJournalEntry<TSubject> : IJournalEntry
    where TSubject : IEntity
{
    TSubject Subject { get; set; }
}

public interface IJournalEntry<TSubject, TObject> : IJournalEntry<TSubject>
    where TSubject : IEntity
    where TObject : IEntity
{
    TObject Object { get; set; }
}

public class JournalEntry : IJournalEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public IJournalEntry Root { get; set; } = null!;
    public IJournalEntry? Parent { get; set; }
    public IJournalEntry? CausedBy { get; set; }
    public long Sequence { get; set; }
    public IRequest? Request { get; set; }
    public IUser? User { get; set; }
    public IApiKey? ApiKey { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 1;
    public JournalPhase Phase { get; set; } = JournalPhase.Fact;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Component { get; set; } = string.Empty;
    public JsonElement Data { get; set; } = JournalJson.EmptyObject();
    public string? OutcomeCode { get; set; }
    public string? OutcomeMessage { get; set; }
    public bool? Retryable { get; set; }
}

public class JournalEntry<TSubject> : JournalEntry, IJournalEntry<TSubject>
    where TSubject : IEntity
{
    public required TSubject Subject { get; set; }
}

public class JournalEntry<TSubject, TObject> : JournalEntry<TSubject>, IJournalEntry<TSubject, TObject>
    where TSubject : IEntity
    where TObject : IEntity
{
    public required TObject Object { get; set; }
}

public sealed record JournalRecord(
    string Id,
    string RootId,
    string? ParentId,
    string? CausedById,
    long Sequence,
    string? RequestId,
    string? UserId,
    string? ApiKeyId,
    string? SubjectType,
    string? SubjectId,
    string? ObjectType,
    string? ObjectId,
    string Kind,
    int SchemaVersion,
    JournalPhase Phase,
    DateTimeOffset Timestamp,
    string Component,
    JsonElement Data,
    string? OutcomeCode,
    string? OutcomeMessage,
    bool? Retryable)
{
    public static JournalRecord FromEntry(IJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Root is null)
            throw new ArgumentException("Journal entries must reference a root entry.", nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.Id))
            throw new ArgumentException("Journal entry ids cannot be empty.", nameof(entry));

        var (subjectType, subjectId, objectType, objectId) = GetRelationship(entry);
        var data = entry.Data.ValueKind == JsonValueKind.Undefined
            ? JournalJson.EmptyObject()
            : entry.Data.Clone();

        return new JournalRecord(
            entry.Id,
            entry.Root.Id,
            entry.Parent?.Id,
            entry.CausedBy?.Id,
            entry.Sequence,
            entry.Request?.Id,
            entry.User?.Id,
            entry.ApiKey?.Id,
            subjectType,
            subjectId,
            objectType,
            objectId,
            entry.Kind,
            entry.SchemaVersion,
            entry.Phase,
            entry.Timestamp,
            entry.Component,
            data,
            entry.OutcomeCode,
            entry.OutcomeMessage,
            entry.Retryable);
    }

    private static (string? SubjectType, string? SubjectId, string? ObjectType, string? ObjectId)
        GetRelationship(IJournalEntry entry)
    {
        var interfaces = entry.GetType().GetInterfaces();
        var binary = interfaces.FirstOrDefault(type =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IJournalEntry<,>));
        var unary = binary is null
            ? interfaces.FirstOrDefault(type =>
                type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IJournalEntry<>))
            : null;

        if (binary is not null)
        {
            var arguments = binary.GetGenericArguments();
            var subjectContract = typeof(IJournalEntry<>).MakeGenericType(arguments[0]);
            var subject = subjectContract.GetProperty(nameof(IJournalEntry<IEntity>.Subject))?.GetValue(entry) as IEntity;
            var @object = binary.GetProperty(nameof(IJournalEntry<IEntity, IEntity>.Object))?.GetValue(entry) as IEntity;
            return (
                GetEntityContractName(arguments[0], subject),
                subject?.Id,
                GetEntityContractName(arguments[1], @object),
                @object?.Id);
        }

        if (unary is not null)
        {
            var argument = unary.GetGenericArguments()[0];
            var subject = unary.GetProperty(nameof(IJournalEntry<IEntity>.Subject))?.GetValue(entry) as IEntity;
            return (GetEntityContractName(argument, subject), subject?.Id, null, null);
        }

        return (null, null, null, null);
    }

    private static string? GetEntityContractName(Type declaredType, IEntity? entity)
    {
        if (declaredType.IsInterface && declaredType != typeof(IEntity))
            return declaredType.FullName;
        if (entity is null)
            return declaredType.FullName;

        var candidates = entity.GetType().GetInterfaces()
            .Where(type => type != typeof(IEntity) && typeof(IEntity).IsAssignableFrom(type))
            .ToArray();
        var mostSpecific = candidates
            .Where(candidate => !candidates.Any(other =>
                candidate != other && candidate.IsAssignableFrom(other)))
            .OrderByDescending(candidate => candidate.Namespace?.StartsWith("Warp.Core", StringComparison.Ordinal) == true)
            .ThenBy(candidate => candidate.FullName, StringComparer.Ordinal)
            .FirstOrDefault();
        return mostSpecific?.FullName ?? declaredType.FullName;
    }
}

public sealed record JournalAppend(
    string RootId,
    long ExpectedSequence,
    string IdempotencyKey,
    IReadOnlyCollection<JournalRecord> Entries);

public sealed record JournalAppendResult(long LastSequence, bool AlreadyApplied);

public sealed record JournalQuery(
    string? RootId = null,
    long? AfterSequence = null,
    DateTimeOffset? AfterTimestamp = null,
    IReadOnlySet<JournalPhase>? Phases = null,
    IReadOnlySet<string>? Kinds = null,
    int Limit = 1000);

public interface IJournalStore
{
    Task<JournalAppendResult> AppendAsync(
        JournalAppend append,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<JournalRecord> ReadAsync(
        JournalQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class JournalConcurrencyException(string message) : InvalidOperationException(message);
public sealed class JournalIdempotencyException(string message) : InvalidOperationException(message);
public sealed class JournalTerminalStateException(string message) : InvalidOperationException(message);

internal static class JournalStoreSupport
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static bool IsTerminal(JournalPhase phase) => phase is
        JournalPhase.Completed or
        JournalPhase.Failed or
        JournalPhase.Cancelled or
        JournalPhase.Indeterminate;

    internal static string Fingerprint(IReadOnlyCollection<JournalRecord> entries)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static IReadOnlyList<JournalRecord> ValidateAppend(JournalAppend append)
    {
        ArgumentNullException.ThrowIfNull(append);
        if (string.IsNullOrWhiteSpace(append.RootId))
            throw new ArgumentException("Root id cannot be empty.", nameof(append));
        if (string.IsNullOrWhiteSpace(append.IdempotencyKey))
            throw new ArgumentException("Idempotency key cannot be empty.", nameof(append));
        if (append.Entries.Count == 0)
            throw new ArgumentException("An append must contain at least one entry.", nameof(append));

        var entries = append.Entries.ToArray();
        if (append.ExpectedSequence == -1)
        {
            if (entries[0].Sequence != 0 || entries[0].Phase != JournalPhase.Started ||
                !StringComparer.Ordinal.Equals(entries[0].Id, append.RootId))
            {
                throw new ArgumentException(
                    "A new journal root must begin at sequence zero with a Started entry whose id is the root id.",
                    nameof(append));
            }
        }
        else if (entries.Any(entry => entry.Phase == JournalPhase.Started))
        {
            throw new ArgumentException("Started entries are valid only when creating a journal root.", nameof(append));
        }

        var expected = append.ExpectedSequence + 1;
        var terminalCount = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!StringComparer.Ordinal.Equals(entry.RootId, append.RootId))
                throw new ArgumentException("Every entry must belong to the append root.", nameof(append));
            if (entry.Sequence != expected++)
                throw new ArgumentException("Append entry sequences must be contiguous and ordered.", nameof(append));
            if (!ids.Add(entry.Id))
                throw new ArgumentException("Append entry ids must be unique.", nameof(append));
            if (IsTerminal(entry.Phase))
                terminalCount++;
        }

        if (terminalCount > 1)
            throw new JournalTerminalStateException("A single append cannot contain multiple terminal entries.");

        return entries;
    }

    internal static IEnumerable<JournalRecord> ApplyQuery(
        IEnumerable<JournalRecord> entries,
        JournalQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Query limit must be positive.");
        if (query.AfterSequence.HasValue && string.IsNullOrWhiteSpace(query.RootId))
            throw new ArgumentException("AfterSequence requires a RootId.", nameof(query));

        var filtered = entries;
        if (!string.IsNullOrWhiteSpace(query.RootId))
            filtered = filtered.Where(entry => StringComparer.Ordinal.Equals(entry.RootId, query.RootId));
        if (query.AfterSequence.HasValue)
            filtered = filtered.Where(entry => entry.Sequence > query.AfterSequence.Value);
        if (query.AfterTimestamp.HasValue)
            filtered = filtered.Where(entry => entry.Timestamp > query.AfterTimestamp.Value);
        if (query.Phases is not null)
            filtered = filtered.Where(entry => query.Phases.Contains(entry.Phase));
        if (query.Kinds is not null)
            filtered = filtered.Where(entry => query.Kinds.Contains(entry.Kind));

        var ordered = string.IsNullOrWhiteSpace(query.RootId)
            ? filtered
                .OrderBy(entry => entry.Timestamp)
                .ThenBy(entry => entry.RootId, StringComparer.Ordinal)
                .ThenBy(entry => entry.Sequence)
            : filtered
                .OrderBy(entry => entry.Sequence)
                .ThenBy(entry => entry.Timestamp);

        return ordered.Take(query.Limit);
    }
}

internal static class JournalJson
{
    internal static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
}

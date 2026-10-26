using System.Text.Json;
using Warp.Core.Data;

namespace Warp.Core.Journal;

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

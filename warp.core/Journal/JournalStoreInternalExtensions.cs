using System.Security.Cryptography;
using System.Text.Json;

namespace Warp.Core.Journal;

internal static class JournalStoreInternalExtensions
{
    internal static bool IsTerminal(this JournalPhase phase) => phase is
        JournalPhase.Completed or
        JournalPhase.Failed or
        JournalPhase.Cancelled or
        JournalPhase.Indeterminate;

    internal static string Fingerprint(this IReadOnlyCollection<JournalRecord> entries)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, JournalJson.SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static IReadOnlyList<JournalRecord> Validate(this JournalAppend append)
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
            if (entry.Phase.IsTerminal())
                terminalCount++;
        }

        if (terminalCount > 1)
            throw new JournalTerminalStateException("A single append cannot contain multiple terminal entries.");

        return entries;
    }

    internal static IEnumerable<JournalRecord> ApplyTo(
        this JournalQuery query,
        IEnumerable<JournalRecord> entries)
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

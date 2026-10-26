namespace Warp.Core.Journal;

public sealed record JournalAppend(
    string RootId,
    long ExpectedSequence,
    string IdempotencyKey,
    IReadOnlyCollection<JournalRecord> Entries);

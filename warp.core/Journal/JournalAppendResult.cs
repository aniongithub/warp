namespace Warp.Core.Journal;

public sealed record JournalAppendResult(long LastSequence, bool AlreadyApplied);

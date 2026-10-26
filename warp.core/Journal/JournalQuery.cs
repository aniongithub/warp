namespace Warp.Core.Journal;

public sealed record JournalQuery(
    string? RootId = null,
    long? AfterSequence = null,
    DateTimeOffset? AfterTimestamp = null,
    IReadOnlySet<JournalPhase>? Phases = null,
    IReadOnlySet<string>? Kinds = null,
    int Limit = 1000);

using System.Text.Json;
using Warp.Core.Data;

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

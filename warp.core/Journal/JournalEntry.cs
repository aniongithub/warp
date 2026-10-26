using System.Text.Json;
using Warp.Core.Data;

namespace Warp.Core.Journal;

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

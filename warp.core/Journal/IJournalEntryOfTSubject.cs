using Warp.Core.Data;

namespace Warp.Core.Journal;

public interface IJournalEntry<TSubject> : IJournalEntry
    where TSubject : IEntity
{
    TSubject Subject { get; set; }
}

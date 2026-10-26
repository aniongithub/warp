using Warp.Core.Data;

namespace Warp.Core.Journal;

public class JournalEntry<TSubject> : JournalEntry, IJournalEntry<TSubject>
    where TSubject : IEntity
{
    public required TSubject Subject { get; set; }
}

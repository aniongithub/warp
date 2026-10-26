using Warp.Core.Data;

namespace Warp.Core.Journal;

public class JournalEntry<TSubject, TObject> : JournalEntry<TSubject>, IJournalEntry<TSubject, TObject>
    where TSubject : IEntity
    where TObject : IEntity
{
    public required TObject Object { get; set; }
}

using Warp.Core.Data;

namespace Warp.Core.Journal;

public interface IJournalEntry<TSubject, TObject> : IJournalEntry<TSubject>
    where TSubject : IEntity
    where TObject : IEntity
{
    TObject Object { get; set; }
}

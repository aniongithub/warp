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

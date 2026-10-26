namespace Warp.Core.Journal;

public interface IJournalStore
{
    Task<JournalAppendResult> AppendAsync(
        JournalAppend append,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<JournalRecord> ReadAsync(
        JournalQuery query,
        CancellationToken cancellationToken = default);
}

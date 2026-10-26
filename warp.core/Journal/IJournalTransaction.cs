namespace Warp.Core.Journal;

public interface IJournalTransaction : IAsyncDisposable
{
    IJournalEntry Root { get; }

    Task RecordAsync(IJournalEntry entry, CancellationToken cancellationToken = default);
    Task RecordAsync(IReadOnlyCollection<IJournalEntry> entries, CancellationToken cancellationToken = default);
    Task CommitAsync(CancellationToken cancellationToken = default);
    Task FailAsync(Exception exception, CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
}

namespace Warp.Core.Journal;

public sealed class JournalIdempotencyException(string message) : InvalidOperationException(message);

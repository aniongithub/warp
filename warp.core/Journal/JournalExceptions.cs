namespace Warp.Core.Journal;

public sealed class JournalConcurrencyException(string message) : InvalidOperationException(message);
public sealed class JournalIdempotencyException(string message) : InvalidOperationException(message);
public sealed class JournalTerminalStateException(string message) : InvalidOperationException(message);

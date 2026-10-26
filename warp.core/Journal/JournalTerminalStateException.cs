namespace Warp.Core.Journal;

public sealed class JournalTerminalStateException(string message) : InvalidOperationException(message);

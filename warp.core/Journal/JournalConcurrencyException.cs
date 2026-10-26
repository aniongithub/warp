namespace Warp.Core.Journal;

public sealed class JournalConcurrencyException(string message) : InvalidOperationException(message);

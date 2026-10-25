using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Warp.Core.Journal;

public sealed class JsonLinesJournalStore : IJournalStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<JournalRecord> _entries = [];
    private readonly Dictionary<string, RootState> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AppliedAppend> _appends = new(StringComparer.Ordinal);
    private long _knownLength;

    public JsonLinesJournalStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Journal file path cannot be empty.", nameof(filePath));

        _filePath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        if (!File.Exists(_filePath))
            File.WriteAllBytes(_filePath, []);

        using var stream = OpenExclusive(CancellationToken.None);
        ReloadFromStream(stream);
    }

    public async Task<JournalAppendResult> AppendAsync(
        JournalAppend append,
        CancellationToken cancellationToken = default)
    {
        var entries = JournalStoreSupport.ValidateAppend(append);
        var fingerprint = JournalStoreSupport.Fingerprint(entries);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = await OpenExclusiveAsync(cancellationToken);
            ReloadFromStream(stream);

            if (_appends.TryGetValue(append.IdempotencyKey, out var applied))
            {
                if (!StringComparer.Ordinal.Equals(applied.Fingerprint, fingerprint) ||
                    !StringComparer.Ordinal.Equals(applied.RootId, append.RootId))
                {
                    throw new JournalIdempotencyException(
                        $"Idempotency key {append.IdempotencyKey} was reused for different journal content.");
                }

                return new JournalAppendResult(applied.LastSequence, true);
            }

            ValidateCurrentState(append, entries);
            var batch = new JsonLineBatch(
                1,
                append.RootId,
                append.ExpectedSequence,
                append.IdempotencyKey,
                fingerprint,
                entries);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, JournalStoreSupport.JsonOptions);

            stream.Seek(0, SeekOrigin.End);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);

            ApplyBatch(batch);
            _knownLength = stream.Length;
            return new JournalAppendResult(entries[^1].Sequence, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<JournalRecord> ReadAsync(
        JournalQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        JournalRecord[] snapshot;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = await OpenExclusiveAsync(cancellationToken);
            if (stream.Length != _knownLength)
                ReloadFromStream(stream);
            snapshot = JournalStoreSupport.ApplyQuery(_entries, query).ToArray();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry with { Data = entry.Data.Clone() };
        }
    }

    private void ValidateCurrentState(JournalAppend append, IReadOnlyList<JournalRecord> entries)
    {
        var state = _roots.GetValueOrDefault(append.RootId) ?? new RootState(-1, false);
        if (state.LastSequence != append.ExpectedSequence)
        {
            throw new JournalConcurrencyException(
                $"Root {append.RootId} expected sequence {append.ExpectedSequence}, " +
                $"but the durable sequence is {state.LastSequence}.");
        }
        if (state.HasTerminal)
            throw new JournalTerminalStateException($"Root {append.RootId} already has a terminal outcome.");
        if (entries.Any(entry => JournalStoreSupport.IsTerminal(entry.Phase)) && state.HasTerminal)
            throw new JournalTerminalStateException($"Root {append.RootId} already has a terminal outcome.");

        var existingIds = _entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var duplicate = entries.FirstOrDefault(entry => existingIds.Contains(entry.Id));
        if (duplicate is not null)
            throw new JournalIdempotencyException($"Journal entry id {duplicate.Id} already exists.");
    }

    private void ReloadFromStream(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        byte[] bytes;
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }

        var completeLength = FindCompleteLength(bytes);
        if (completeLength != bytes.Length)
        {
            stream.SetLength(completeLength);
            stream.Flush(flushToDisk: true);
            bytes = bytes[..completeLength];
        }

        _entries.Clear();
        _roots.Clear();
        _appends.Clear();

        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n')
                continue;

            var length = index - start;
            if (length > 0)
            {
                var batch = JsonSerializer.Deserialize<JsonLineBatch>(
                    bytes.AsSpan(start, length),
                    JournalStoreSupport.JsonOptions)
                    ?? throw new InvalidDataException("Journal contains an empty JSONL batch.");
                ValidateRecoveredBatch(batch);
                ApplyBatch(batch);
            }
            start = index + 1;
        }

        _knownLength = completeLength;
        stream.Seek(0, SeekOrigin.End);
    }

    private void ValidateRecoveredBatch(JsonLineBatch batch)
    {
        if (batch.Version != 1)
            throw new InvalidDataException($"Unsupported JSONL journal version {batch.Version}.");

        var append = new JournalAppend(
            batch.RootId,
            batch.ExpectedSequence,
            batch.IdempotencyKey,
            batch.Entries);
        var entries = JournalStoreSupport.ValidateAppend(append);
        var fingerprint = JournalStoreSupport.Fingerprint(entries);
        if (!StringComparer.Ordinal.Equals(fingerprint, batch.Fingerprint))
            throw new InvalidDataException($"Journal batch {batch.IdempotencyKey} failed its fingerprint check.");

        if (_appends.TryGetValue(batch.IdempotencyKey, out var existing))
        {
            if (!StringComparer.Ordinal.Equals(existing.Fingerprint, batch.Fingerprint))
                throw new InvalidDataException($"Journal idempotency key {batch.IdempotencyKey} has conflicting content.");
            return;
        }

        ValidateCurrentState(append, entries);
    }

    private void ApplyBatch(JsonLineBatch batch)
    {
        if (_appends.ContainsKey(batch.IdempotencyKey))
            return;

        var entries = batch.Entries.ToArray();
        _entries.AddRange(entries);
        var hasTerminal = entries.Any(entry => JournalStoreSupport.IsTerminal(entry.Phase));
        _roots.TryGetValue(batch.RootId, out var current);
        _roots[batch.RootId] = new RootState(
            entries[^1].Sequence,
            current?.HasTerminal == true || hasTerminal);
        _appends[batch.IdempotencyKey] = new AppliedAppend(
            batch.RootId,
            batch.Fingerprint,
            entries[^1].Sequence);
    }

    private FileStream OpenExclusive(CancellationToken cancellationToken) =>
        OpenExclusiveAsync(cancellationToken).GetAwaiter().GetResult();

    private async Task<FileStream> OpenExclusiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _filePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(20, cancellationToken);
            }
        }
    }

    private static int FindCompleteLength(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes[^1] == (byte)'\n')
            return bytes.Length;

        for (var index = bytes.Length - 1; index >= 0; index--)
        {
            if (bytes[index] == (byte)'\n')
                return index + 1;
        }

        return 0;
    }

    private sealed record JsonLineBatch(
        int Version,
        string RootId,
        long ExpectedSequence,
        string IdempotencyKey,
        string Fingerprint,
        IReadOnlyCollection<JournalRecord> Entries);

    private sealed record RootState(long LastSequence, bool HasTerminal);
    private sealed record AppliedAppend(string RootId, string Fingerprint, long LastSequence);
}

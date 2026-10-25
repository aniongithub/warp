using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Warp.Core.Journal;

public sealed class SqliteJournalStore : IJournalStore
{
    private readonly string _connectionString;

    public SqliteJournalStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Database file path cannot be empty.", nameof(filePath));

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        _connectionString = $"Data Source={fullPath}";
        InitializeDatabase();
    }

    public async Task<JournalAppendResult> AppendAsync(
        JournalAppend append,
        CancellationToken cancellationToken = default)
    {
        var entries = JournalStoreSupport.ValidateAppend(append);
        var fingerprint = JournalStoreSupport.Fingerprint(entries);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken);
        await ExecuteAsync(connection, "BEGIN IMMEDIATE;", cancellationToken);
        try
        {
            var existing = await GetAppliedAppendAsync(connection, append.IdempotencyKey, cancellationToken);
            if (existing is not null)
            {
                if (!StringComparer.Ordinal.Equals(existing.RootId, append.RootId) ||
                    !StringComparer.Ordinal.Equals(existing.Fingerprint, fingerprint))
                {
                    throw new JournalIdempotencyException(
                        $"Idempotency key {append.IdempotencyKey} was reused for different journal content.");
                }

                await ExecuteAsync(connection, "COMMIT;", cancellationToken);
                return new JournalAppendResult(existing.LastSequence, true);
            }

            var state = await GetRootStateAsync(connection, append.RootId, cancellationToken);
            if (state.LastSequence != append.ExpectedSequence)
            {
                throw new JournalConcurrencyException(
                    $"Root {append.RootId} expected sequence {append.ExpectedSequence}, " +
                    $"but the durable sequence is {state.LastSequence}.");
            }
            if (state.HasTerminal)
                throw new JournalTerminalStateException($"Root {append.RootId} already has a terminal outcome.");

            foreach (var entry in entries)
                await InsertEntryAsync(connection, entry, cancellationToken);

            var insertAppend = connection.CreateCommand();
            insertAppend.CommandText = @"
                INSERT INTO JournalAppends (IdempotencyKey, RootId, Fingerprint, LastSequence)
                VALUES ($key, $rootId, $fingerprint, $lastSequence);";
            insertAppend.Parameters.AddWithValue("$key", append.IdempotencyKey);
            insertAppend.Parameters.AddWithValue("$rootId", append.RootId);
            insertAppend.Parameters.AddWithValue("$fingerprint", fingerprint);
            insertAppend.Parameters.AddWithValue("$lastSequence", entries[^1].Sequence);
            await insertAppend.ExecuteNonQueryAsync(cancellationToken);

            await ExecuteAsync(connection, "COMMIT;", cancellationToken);
            return new JournalAppendResult(entries[^1].Sequence, false);
        }
        catch
        {
            try
            {
                await ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None);
            }
            catch (SqliteException)
            {
                // The original failure is more useful than a rollback error after SQLite ended the transaction.
            }
            throw;
        }
    }

    public async IAsyncEnumerable<JournalRecord> ReadAsync(
        JournalQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = JournalStoreSupport.ApplyQuery(Array.Empty<JournalRecord>(), query);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        var predicates = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.RootId))
        {
            predicates.Add("RootId = $rootId");
            command.Parameters.AddWithValue("$rootId", query.RootId);
        }
        if (query.AfterSequence.HasValue)
        {
            predicates.Add("Sequence > $afterSequence");
            command.Parameters.AddWithValue("$afterSequence", query.AfterSequence.Value);
        }
        if (query.AfterTimestamp.HasValue)
        {
            predicates.Add("Timestamp > $afterTimestamp");
            command.Parameters.AddWithValue(
                "$afterTimestamp",
                query.AfterTimestamp.Value.ToUniversalTime().ToString("O"));
        }
        AddSetPredicate(command, predicates, "Phase", "phase", query.Phases?.Select(value => value.ToString()));
        AddSetPredicate(command, predicates, "Kind", "kind", query.Kinds);

        var orderBy = string.IsNullOrWhiteSpace(query.RootId)
            ? "Timestamp, RootId, Sequence"
            : "Sequence, Timestamp";
        command.CommandText = $@"
            SELECT Id, RootId, ParentId, CausedById, Sequence,
                   RequestId, UserId, ApiKeyId,
                   SubjectType, SubjectId, ObjectType, ObjectId,
                   Kind, SchemaVersion, Phase, Timestamp, Component, Data,
                   OutcomeCode, OutcomeMessage, Retryable
            FROM JournalEntries
            {(predicates.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", predicates))}
            ORDER BY {orderBy}
            LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", query.Limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var data = JsonDocument.Parse(reader.GetString(17));
            yield return new JournalRecord(
                reader.GetString(0),
                reader.GetString(1),
                GetNullableString(reader, 2),
                GetNullableString(reader, 3),
                reader.GetInt64(4),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                GetNullableString(reader, 8),
                GetNullableString(reader, 9),
                GetNullableString(reader, 10),
                GetNullableString(reader, 11),
                reader.GetString(12),
                reader.GetInt32(13),
                Enum.Parse<JournalPhase>(reader.GetString(14), ignoreCase: false),
                DateTimeOffset.Parse(reader.GetString(15)),
                reader.GetString(16),
                data.RootElement.Clone(),
                GetNullableString(reader, 18),
                GetNullableString(reader, 19),
                reader.IsDBNull(20) ? null : reader.GetInt32(20) != 0);
        }
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS JournalEntries (
                Id TEXT NOT NULL UNIQUE,
                RootId TEXT NOT NULL,
                ParentId TEXT NULL,
                CausedById TEXT NULL,
                Sequence INTEGER NOT NULL,
                RequestId TEXT NULL,
                UserId TEXT NULL,
                ApiKeyId TEXT NULL,
                SubjectType TEXT NULL,
                SubjectId TEXT NULL,
                ObjectType TEXT NULL,
                ObjectId TEXT NULL,
                Kind TEXT NOT NULL,
                SchemaVersion INTEGER NOT NULL,
                Phase TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                Component TEXT NOT NULL,
                Data TEXT NOT NULL,
                OutcomeCode TEXT NULL,
                OutcomeMessage TEXT NULL,
                Retryable INTEGER NULL,
                PRIMARY KEY (RootId, Sequence)
            );

            CREATE TABLE IF NOT EXISTS JournalAppends (
                IdempotencyKey TEXT PRIMARY KEY,
                RootId TEXT NOT NULL,
                Fingerprint TEXT NOT NULL,
                LastSequence INTEGER NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_JournalEntries_RootTerminal
            ON JournalEntries (RootId)
            WHERE Phase IN ('Completed', 'Failed', 'Cancelled', 'Indeterminate');

            CREATE INDEX IF NOT EXISTS IX_JournalEntries_Timestamp
            ON JournalEntries (Timestamp, RootId, Sequence);

            CREATE INDEX IF NOT EXISTS IX_JournalEntries_Kind
            ON JournalEntries (Kind, Timestamp);";
        command.ExecuteNonQuery();
    }

    private static async Task InsertEntryAsync(
        SqliteConnection connection,
        JournalRecord entry,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO JournalEntries (
                Id, RootId, ParentId, CausedById, Sequence,
                RequestId, UserId, ApiKeyId,
                SubjectType, SubjectId, ObjectType, ObjectId,
                Kind, SchemaVersion, Phase, Timestamp, Component, Data,
                OutcomeCode, OutcomeMessage, Retryable)
            VALUES (
                $id, $rootId, $parentId, $causedById, $sequence,
                $requestId, $userId, $apiKeyId,
                $subjectType, $subjectId, $objectType, $objectId,
                $kind, $schemaVersion, $phase, $timestamp, $component, $data,
                $outcomeCode, $outcomeMessage, $retryable);";
        AddParameter(command, "$id", entry.Id);
        AddParameter(command, "$rootId", entry.RootId);
        AddParameter(command, "$parentId", entry.ParentId);
        AddParameter(command, "$causedById", entry.CausedById);
        AddParameter(command, "$sequence", entry.Sequence);
        AddParameter(command, "$requestId", entry.RequestId);
        AddParameter(command, "$userId", entry.UserId);
        AddParameter(command, "$apiKeyId", entry.ApiKeyId);
        AddParameter(command, "$subjectType", entry.SubjectType);
        AddParameter(command, "$subjectId", entry.SubjectId);
        AddParameter(command, "$objectType", entry.ObjectType);
        AddParameter(command, "$objectId", entry.ObjectId);
        AddParameter(command, "$kind", entry.Kind);
        AddParameter(command, "$schemaVersion", entry.SchemaVersion);
        AddParameter(command, "$phase", entry.Phase.ToString());
        AddParameter(command, "$timestamp", entry.Timestamp.ToUniversalTime().ToString("O"));
        AddParameter(command, "$component", entry.Component);
        AddParameter(command, "$data", entry.Data.GetRawText());
        AddParameter(command, "$outcomeCode", entry.OutcomeCode);
        AddParameter(command, "$outcomeMessage", entry.OutcomeMessage);
        AddParameter(command, "$retryable", entry.Retryable.HasValue ? entry.Retryable.Value ? 1 : 0 : null);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AppliedAppend?> GetAppliedAppendAsync(
        SqliteConnection connection,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT RootId, Fingerprint, LastSequence
            FROM JournalAppends
            WHERE IdempotencyKey = $key;";
        command.Parameters.AddWithValue("$key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AppliedAppend(reader.GetString(0), reader.GetString(1), reader.GetInt64(2))
            : null;
    }

    private static async Task<RootState> GetRootStateAsync(
        SqliteConnection connection,
        string rootId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COALESCE(MAX(Sequence), -1),
                   COALESCE(MAX(CASE WHEN Phase IN ('Completed', 'Failed', 'Cancelled', 'Indeterminate') THEN 1 ELSE 0 END), 0)
            FROM JournalEntries
            WHERE RootId = $rootId;";
        command.Parameters.AddWithValue("$rootId", rootId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new RootState(reader.GetInt64(0), reader.GetInt32(1) != 0);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddSetPredicate(
        SqliteCommand command,
        ICollection<string> predicates,
        string column,
        string parameterPrefix,
        IEnumerable<string>? values)
    {
        if (values is null)
            return;
        var array = values.Distinct(StringComparer.Ordinal).ToArray();
        if (array.Length == 0)
        {
            predicates.Add("1 = 0");
            return;
        }

        var names = new List<string>(array.Length);
        for (var index = 0; index < array.Length; index++)
        {
            var name = $"${parameterPrefix}{index}";
            names.Add(name);
            command.Parameters.AddWithValue(name, array[index]);
        }
        predicates.Add($"{column} IN ({string.Join(", ", names)})");
    }

    private static void AddParameter(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record RootState(long LastSequence, bool HasTerminal);
    private sealed record AppliedAppend(string RootId, string Fingerprint, long LastSequence);
}

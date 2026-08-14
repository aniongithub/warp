using System.Linq.Expressions;
using Npgsql;

namespace Warp.Core.Data.Contexts;

public class PostgreSqlDataContext : IDataContext
{
    private readonly string _connectionString;

    public PostgreSqlDataContext(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));

        _connectionString = connectionString;
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        try
        {
            // Try to connect directly to the target database first
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            CreateTables(conn);
        }
        catch (PostgresException ex) when (ex.SqlState == "3D000") // Database does not exist
        {
            // Only try to create database if it doesn't exist
            CreateDatabaseIfNotExists();
            
            // Connect again and create tables
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            CreateTables(conn);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize database: {ex.Message}", ex);
        }
    }

    private void CreateDatabaseIfNotExists()
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        var databaseName = builder.Database;
        
        if (string.IsNullOrEmpty(databaseName))
        {
            throw new InvalidOperationException("Database name is required in connection string");
        }
        
        // Connect to the maintenance database to check/create target database
        builder.Database = "postgres";
        var adminConnectionString = builder.ToString();
        
        using var adminConn = new NpgsqlConnection(adminConnectionString);
        adminConn.Open();
        
        // Check if database exists
        using var checkDbCmd = adminConn.CreateCommand();
        checkDbCmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @dbname";
        checkDbCmd.Parameters.AddWithValue("@dbname", databaseName);
        var dbExists = checkDbCmd.ExecuteScalar() != null;
        
        if (!dbExists)
        {
            // Create database if it doesn't exist
            using var createDbCmd = adminConn.CreateCommand();
            createDbCmd.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            createDbCmd.ExecuteNonQuery();
        }
    }

    private void CreateTables(NpgsqlConnection conn)
    {
        var tableCreationCommands = new[]
        {
            @"CREATE TABLE IF NOT EXISTS Users (
                Id TEXT PRIMARY KEY,
                Email TEXT,
                Permissions TEXT
            )",
            @"CREATE TABLE IF NOT EXISTS ApiKeys (
                Id TEXT PRIMARY KEY,
                Key TEXT,
                Owner TEXT,
                IsActive BOOLEAN,
                Permissions TEXT,
                RateLimitHz REAL
            )",
            @"CREATE TABLE IF NOT EXISTS Requests (
                Id TEXT PRIMARY KEY,
                Key TEXT,
                LastUsed TIMESTAMP,
                LastRate REAL
            )",
            @"CREATE TABLE IF NOT EXISTS Events (
                Id TEXT PRIMARY KEY,
                Key TEXT,
                EventType TEXT,
                Timestamp TIMESTAMP
            )",
            @"CREATE TABLE IF NOT EXISTS Quotas (
                Id TEXT PRIMARY KEY,
                Key TEXT,
                QuotaName TEXT,
                Used REAL,
                QuotaLimit REAL,
                Type TEXT
            )"
        };

        foreach (var commandText in tableCreationCommands)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = commandText;
            cmd.ExecuteNonQuery();
        }
    }
        
    public IQueryable<IUser> Users => GetUsers().AsQueryable();
    public IQueryable<IApiKey> ApiKeys => GetApiKeys().AsQueryable();
    public IQueryable<IRequest> Requests => GetRequests().AsQueryable();
    public IQueryable<IQuota> Quotas => GetQuotas().AsQueryable();

    private List<IUser> GetUsers()
    {
        var users = new List<IUser>();
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Email, Permissions FROM Users";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            users.Add(new User
            {
                Id = reader.GetString(0),
                Email = reader.GetString(1),
                Permissions = reader.IsDBNull(2) ? new List<string>() : reader.GetString(2).Split(',').Where(p => !string.IsNullOrWhiteSpace(p)).ToList()
            });
        }
        return users;
    }

    private List<IApiKey> GetApiKeys()
    {
        var keys = new List<IApiKey>();
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Key, Owner, IsActive, Permissions, RateLimitHz FROM ApiKeys";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            keys.Add(new ApiKey
            {
                Id = reader.GetString(0),
                Key = reader.GetString(1),
                Owner = reader.GetString(2),
                IsActive = reader.GetBoolean(3),
                Permissions = reader.IsDBNull(4) ? new List<string>() : reader.GetString(4).Split(',').Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
                RateLimitHz = reader.GetFloat(5)
            });
        }
        return keys;
    }

    private List<IRequest> GetRequests()
    {
        var reqs = new List<IRequest>();
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Key, LastUsed, LastRate FROM Requests";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            reqs.Add(new Request
            {
                Id = reader.GetString(0),
                Key = reader.GetString(1),
                LastUsed = reader.GetDateTime(2),
                LastRate = reader.GetFloat(3)
            });
        }
        return reqs;
    }

    private List<IQuota> GetQuotas()
    {
        var quotas = new List<IQuota>();
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Key, QuotaName, Used, QuotaLimit, Type FROM Quotas";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            quotas.Add(new Quota
            {
                Id = reader.GetString(0),
                Key = reader.GetString(1),
                QuotaName = reader.GetString(2),
                Used = reader.GetFloat(3),
                Limit = reader.GetFloat(4), // Map QuotaLimit to Limit property
                Type = reader.GetString(5)
            });
        }
        return quotas;
    }

    public async Task SaveAsync<T>(T entity) where T : IEntity
    {
        switch (entity)
        {
            case IUser user:
                await UpsertUserAsync(user);
                break;
            case IApiKey apiKey:
                await UpsertApiKeyAsync(apiKey);
                break;
            case IRequest request:
                await UpsertRequestAsync(request);
                break;
            case IQuota quota:
                await UpsertQuotaAsync(quota);
                break;
            default:
                throw new NotSupportedException($"Entity type {typeof(T).Name} not supported.");
        }
    }

    public Task UpsertAsync<T>(T entity, Expression<Func<T, bool>> filter) where T : IEntity
    {
        // For simplicity, just call SaveAsync
        return SaveAsync(entity);
    }

    public async Task<QuotaConsumeResult> TryConsumeQuotaAsync(string quotaId, float amount)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();

        // A single conditional UPDATE performs the check-and-increment atomically. For prepaid
        // quotas the increment is applied only while it keeps Used within QuotaLimit.
        var update = conn.CreateCommand();
        update.Transaction = tx;
        update.CommandText = @"
        UPDATE Quotas SET Used = Used + $1
        WHERE Id = $2 AND (Type <> 'prepaid' OR Used + $1 <= QuotaLimit);";
        update.Parameters.AddWithValue(amount);
        update.Parameters.AddWithValue(quotaId);
        var rows = await update.ExecuteNonQueryAsync();

        QuotaConsumeResult result;
        if (rows > 0)
        {
            result = QuotaConsumeResult.Consumed;
        }
        else
        {
            var check = conn.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "SELECT COUNT(*) FROM Quotas WHERE Id = $1;";
            check.Parameters.AddWithValue(quotaId);
            var count = Convert.ToInt64(await check.ExecuteScalarAsync());
            result = count > 0 ? QuotaConsumeResult.LimitExceeded : QuotaConsumeResult.NotFound;
        }

        await tx.CommitAsync();
        return result;
    }

    public async Task SettleQuotaAsync(string quotaId, float delta)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();

        // Unconditional atomic adjustment: the reservation was already admitted, so settling never
        // fails on the prepaid limit. GREATEST(0, ...) floors Used so an over-refund cannot go negative.
        var update = conn.CreateCommand();
        update.Transaction = tx;
        update.CommandText = @"
        UPDATE Quotas SET Used = GREATEST(0, Used + $1)
        WHERE Id = $2;";
        update.Parameters.AddWithValue(delta);
        update.Parameters.AddWithValue(quotaId);
        await update.ExecuteNonQueryAsync();

        await tx.CommitAsync();
    }

    public async Task GrantQuotaAsync(string quotaId, float amount)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();

        // A single conditional-free UPDATE inside the transaction performs the increment atomically,
        // so concurrent grants on the money-in path cannot lose updates.
        var update = conn.CreateCommand();
        update.Transaction = tx;
        update.CommandText = @"
        UPDATE Quotas SET QuotaLimit = QuotaLimit + $1
        WHERE Id = $2;";
        update.Parameters.AddWithValue(amount);
        update.Parameters.AddWithValue(quotaId);
        await update.ExecuteNonQueryAsync();

        await tx.CommitAsync();
    }

    public async Task<bool> TryConsumeRateLimitAsync(string key, float rateLimitHz, float maxTokens, DateTime now)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();

        string? id = null;
        DateTime lastUsed = now;
        float lastRate = 0f;
        var exists = false;

        // SELECT ... FOR UPDATE cannot lock a row that does not exist yet, so concurrent first-hits
        // on a cold (never-seen) key would each observe an empty bucket, each compute a full token
        // allowance and all be admitted — overrunning the configured capacity. A transaction-scoped
        // advisory lock keyed on the bucket serializes those cold-start callers as well; it is
        // released automatically when the transaction commits/rolls back. A hash collision only
        // serializes two unrelated keys (a minor, safe concurrency reduction) and never affects
        // correctness.
        var advisoryLock = conn.CreateCommand();
        advisoryLock.Transaction = tx;
        advisoryLock.CommandText = "SELECT pg_advisory_xact_lock(hashtext($1)::bigint);";
        advisoryLock.Parameters.AddWithValue(key);
        await advisoryLock.ExecuteNonQueryAsync();

        // With the per-key advisory lock held, the existing bucket row (if any) is read under the
        // same serialized section so token decrements cannot be lost.
        var select = conn.CreateCommand();
        select.Transaction = tx;
        select.CommandText = "SELECT Id, LastUsed, LastRate FROM Requests WHERE Key = $1 ORDER BY LastUsed DESC LIMIT 1 FOR UPDATE;";
        select.Parameters.AddWithValue(key);
        using (var reader = await select.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                exists = true;
                id = reader.GetString(0);
                lastUsed = reader.GetDateTime(1);
                lastRate = reader.GetFloat(2);
            }
        }

        if (!RateLimitTokenBucket.TryConsume(exists, lastUsed, lastRate, now, rateLimitHz, maxTokens, out var remaining))
        {
            await tx.CommitAsync();
            return false;
        }

        if (exists)
        {
            var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE Requests SET LastUsed = $1, LastRate = $2 WHERE Id = $3;";
            update.Parameters.AddWithValue(now);
            update.Parameters.AddWithValue(remaining);
            update.Parameters.AddWithValue(id!);
            await update.ExecuteNonQueryAsync();
        }
        else
        {
            var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO Requests (Id, Key, LastUsed, LastRate) VALUES ($1, $2, $3, $4);";
            insert.Parameters.AddWithValue(Guid.NewGuid().ToString());
            insert.Parameters.AddWithValue(key);
            insert.Parameters.AddWithValue(now);
            insert.Parameters.AddWithValue(remaining);
            await insert.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return true;
    }

    private async Task UpsertUserAsync(IUser user)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        INSERT INTO Users (Id, Email, Permissions) VALUES ($1, $2, $3)
        ON CONFLICT(Id) DO UPDATE SET Email=$2, Permissions=$3;";
        cmd.Parameters.AddWithValue(user.Id);
        cmd.Parameters.AddWithValue(user.Email);
        cmd.Parameters.AddWithValue(string.Join(",", user.Permissions ?? new List<string>()));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpsertApiKeyAsync(IApiKey apiKey)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        INSERT INTO ApiKeys (Id, Key, Owner, IsActive, Permissions, RateLimitHz) VALUES ($1, $2, $3, $4, $5, $6)
        ON CONFLICT(Id) DO UPDATE SET Key=$2, Owner=$3, IsActive=$4, Permissions=$5, RateLimitHz=$6;";
        cmd.Parameters.AddWithValue(apiKey.Id);
        cmd.Parameters.AddWithValue(apiKey.Key);
        cmd.Parameters.AddWithValue(apiKey.Owner);
        cmd.Parameters.AddWithValue(apiKey.IsActive);
        cmd.Parameters.AddWithValue(string.Join(",", apiKey.Permissions ?? new List<string>()));
        cmd.Parameters.AddWithValue(apiKey.RateLimitHz);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpsertRequestAsync(IRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        INSERT INTO Requests (Id, Key, LastUsed, LastRate) VALUES ($1, $2, $3, $4)
        ON CONFLICT(Id) DO UPDATE SET Key=$2, LastUsed=$3, LastRate=$4;";
        cmd.Parameters.AddWithValue(request.Id);
        cmd.Parameters.AddWithValue(request.Key);
        cmd.Parameters.AddWithValue(request.LastUsed);
        cmd.Parameters.AddWithValue(request.LastRate);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpsertQuotaAsync(IQuota quota)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        INSERT INTO Quotas (Id, Key, QuotaName, Used, QuotaLimit, Type) VALUES ($1, $2, $3, $4, $5, $6)
        ON CONFLICT(Id) DO UPDATE SET Key=$2, QuotaName=$3, Used=$4, QuotaLimit=$5, Type=$6;";
        cmd.Parameters.AddWithValue(quota.Id);
        cmd.Parameters.AddWithValue(quota.Key);
        cmd.Parameters.AddWithValue(quota.QuotaName);
        cmd.Parameters.AddWithValue(quota.Used);
        cmd.Parameters.AddWithValue(quota.Limit);
        cmd.Parameters.AddWithValue(quota.Type);
        await cmd.ExecuteNonQueryAsync();
    }

    // Concrete implementations for serialization (identical to SqliteDataContext)
    public class User : IUser
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Email { get; set; } = "";
        public List<string> Permissions { get; set; } = new();
        List<string> IUser.Permissions => Permissions;
    }

    public class ApiKey : IApiKey
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Key { get; set; } = "";
        public string Owner { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public List<string> Permissions { get; set; } = new();
        public float RateLimitHz { get; set; } = 1.0f;
    }

    public class Request : IRequest
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Key { get; set; } = "";
        public DateTime LastUsed { get; set; } = DateTime.MinValue;
        public float LastRate { get; set; } = 0;
    }

    public class Quota : IQuota
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Key { get; set; } = "";
        public string QuotaName { get; set; } = "";
        public float Used { get; set; } = 0;
        public float Limit { get; set; } = 0; // This maps to QuotaLimit in DB
        public string Type { get; set; } = "prepaid";
    }

    public IUser CreateUser() => new User();
    public IApiKey CreateApiKey() => new ApiKey();
    public IRequest CreateRequest() => new Request();
    public IQuota CreateQuota() => new Quota();
}

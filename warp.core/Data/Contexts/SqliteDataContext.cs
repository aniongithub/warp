using System.Linq.Expressions;
using Microsoft.Data.Sqlite;

namespace Warp.Core.Data.Contexts;

public class SqliteDataContext : IDataContext
{
    private readonly string _connectionString;

    public SqliteDataContext(string filePath)
    {
        // Ensure the file path is valid and accessible, otherwise create the directory
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Database file path cannot be null or empty.", nameof(filePath));
        var dbDirectory = Path.GetDirectoryName(filePath);
        if (dbDirectory != null && !Directory.Exists(dbDirectory))
            Directory.CreateDirectory(dbDirectory);

        _connectionString = $"Data Source={filePath}";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        // Update DB schema: change ProductTier to Permissions (TEXT, comma-separated)
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS Users (
            Id TEXT PRIMARY KEY,
            Email TEXT,
            Permissions TEXT
        );
        CREATE TABLE IF NOT EXISTS ApiKeys (
            Id TEXT PRIMARY KEY,
            Key TEXT,
            Owner TEXT,
            IsActive INTEGER,
            Permissions TEXT,
            RateLimitHz REAL
        );
        CREATE TABLE IF NOT EXISTS Requests (
            Id TEXT PRIMARY KEY,
            Key TEXT,
            LastUsed TEXT,
            LastRate REAL
        );
        CREATE TABLE IF NOT EXISTS Events (
            Id TEXT PRIMARY KEY,
            Key TEXT,
            EventType TEXT,
            Timestamp TEXT
        );
        CREATE TABLE IF NOT EXISTS Quotas (
            Id TEXT PRIMARY KEY,
            Key TEXT,
            QuotaName TEXT,
            Used REAL,
            QuotaLimit REAL,
            Type TEXT
        );
        ";
        cmd.ExecuteNonQuery();
    }
        
    public IQueryable<IUser> Users => GetUsers().AsQueryable();
    public IQueryable<IApiKey> ApiKeys => GetApiKeys().AsQueryable();
    public IQueryable<IRequest> Requests => GetRequests().AsQueryable();
    public IQueryable<IQuota> Quotas => GetQuotas().AsQueryable();

    private List<IUser> GetUsers()
    {
        var users = new List<IUser>();
        using var conn = new SqliteConnection(_connectionString);
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
        using var conn = new SqliteConnection(_connectionString);
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
                IsActive = reader.GetInt32(3) != 0,
                Permissions = reader.IsDBNull(4) ? new List<string>() : reader.GetString(4).Split(',').Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
                RateLimitHz = (float)reader.GetDouble(5)
            });
        }
        return keys;
    }

    private List<IRequest> GetRequests()
    {
        var reqs = new List<IRequest>();
        using var conn = new SqliteConnection(_connectionString);
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
                LastUsed = DateTime.Parse(reader.GetString(2)),
                LastRate = (float)reader.GetDouble(3)
            });
        }
        return reqs;
    }

    private List<IQuota> GetQuotas()
    {
        var quotas = new List<IQuota>();
        using var conn = new SqliteConnection(_connectionString);
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
                Used = (float)reader.GetDouble(3),
                Limit = (float)reader.GetDouble(4), // Map QuotaLimit to Limit property
                Type = reader.GetString(5)
            });
        }
        return quotas;
    }

    public Task SaveAsync<T>(T entity) where T : IEntity
    {
        switch (entity)
        {
            case IUser user:
                UpsertUser(user);
                break;
            case IApiKey apiKey:
                UpsertApiKey(apiKey);
                break;
            case IRequest request:
                UpsertRequest(request);
                break;
            case IQuota quota:
                UpsertQuota(quota);
                break;
            default:
                throw new NotSupportedException($"Entity type {typeof(T).Name} not supported.");
        }
        return Task.CompletedTask;
    }

    public Task UpsertAsync<T>(T entity, Expression<Func<T, bool>> filter) where T : IEntity
    {
        // For simplicity, just call SaveAsync
        return SaveAsync(entity);
    }

    private void UpsertUser(IUser user)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        INSERT INTO Users (Id, Email, Permissions) VALUES ($id, $email, $permissions)
        ON CONFLICT(Id) DO UPDATE SET Email=$email, Permissions=$permissions;";
        cmd.Parameters.AddWithValue("$id", user.Id);
        cmd.Parameters.AddWithValue("$email", user.Email);
        cmd.Parameters.AddWithValue("$permissions", string.Join(",", user.Permissions ?? new List<string>()));
        cmd.ExecuteNonQuery();
    }

    private void UpsertApiKey(IApiKey apiKey)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        INSERT INTO ApiKeys (Id, Key, Owner, IsActive, Permissions, RateLimitHz) VALUES ($id, $key, $owner, $isActive, $permissions, $rateLimitHz)
        ON CONFLICT(Id) DO UPDATE SET Key=$key, Owner=$owner, IsActive=$isActive, Permissions=$permissions, RateLimitHz=$rateLimitHz;";
        cmd.Parameters.AddWithValue("$id", apiKey.Id);
        cmd.Parameters.AddWithValue("$key", apiKey.Key);
        cmd.Parameters.AddWithValue("$owner", apiKey.Owner);
        cmd.Parameters.AddWithValue("$isActive", apiKey.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$permissions", string.Join(",", apiKey.Permissions ?? new List<string>()));
        cmd.Parameters.AddWithValue("$rateLimitHz", apiKey.RateLimitHz);
        cmd.ExecuteNonQuery();
    }

    private void UpsertRequest(IRequest request)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        INSERT INTO Requests (Id, Key, LastUsed, LastRate) VALUES ($id, $key, $lastUsed, $lastRate)
        ON CONFLICT(Id) DO UPDATE SET Key=$key, LastUsed=$lastUsed, LastRate=$lastRate;";
        cmd.Parameters.AddWithValue("$id", request.Id);
        cmd.Parameters.AddWithValue("$key", request.Key);
        cmd.Parameters.AddWithValue("$lastUsed", request.LastUsed.ToString("o"));
        cmd.Parameters.AddWithValue("$lastRate", request.LastRate);
        cmd.ExecuteNonQuery();
    }

    private void UpsertQuota(IQuota quota)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        INSERT INTO Quotas (Id, Key, QuotaName, Used, QuotaLimit, Type) VALUES ($id, $key, $quotaName, $used, $quotaLimit, $type)
        ON CONFLICT(Id) DO UPDATE SET Key=$key, QuotaName=$quotaName, Used=$used, QuotaLimit=$quotaLimit, Type=$type;";
        cmd.Parameters.AddWithValue("$id", quota.Id);
        cmd.Parameters.AddWithValue("$key", quota.Key);
        cmd.Parameters.AddWithValue("$quotaName", quota.QuotaName);
        cmd.Parameters.AddWithValue("$used", quota.Used);
        cmd.Parameters.AddWithValue("$quotaLimit", quota.Limit);
        cmd.Parameters.AddWithValue("$type", quota.Type);
        cmd.ExecuteNonQuery();
    }

    // Concrete implementations for serialization
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

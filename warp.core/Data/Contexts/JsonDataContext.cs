using System.Linq.Expressions;
using System.Text.Json;

namespace Warp.Core.Data.Contexts;

public class JsonDataContext : IDataContext
{
    private readonly string _filePath;
    private DataStore _store;

    // Serializes atomic read-modify-write operations within this process. The JSON backend is
    // single-instance only, so a process-wide lock is sufficient to close the metering races.
    private readonly object _syncRoot = new();

    private FileSystemWatcher? _watcher;
    private DateTime _lastReload = DateTime.MinValue;

    public JsonDataContext(string filePath)
    {
        _filePath = filePath;
        _store = File.Exists(_filePath)
            ? JsonSerializer.Deserialize<DataStore>(File.ReadAllText(_filePath), JsonOptions()) ?? new DataStore()
            : new DataStore();
        SetupWatcher();
    }

    private void SetupWatcher()
    {
        var dir = string.IsNullOrEmpty(Path.GetDirectoryName(_filePath)) ? "." : Path.GetDirectoryName(_filePath)!;
        var file = Path.GetFileName(_filePath);
        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };
        _watcher.Changed += (s, e) => ReloadFromFile();
        _watcher.Created += (s, e) => ReloadFromFile();
        _watcher.Renamed += (s, e) => ReloadFromFile();
        _watcher.EnableRaisingEvents = true;
    }

    private void ReloadFromFile()
    {
        // Prevent multiple reloads in quick succession
        var now = DateTime.UtcNow;
        if ((now - _lastReload).TotalMilliseconds < 200) return;
        _lastReload = now;
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var newStore = JsonSerializer.Deserialize<DataStore>(json, JsonOptions());
                if (newStore != null)
                    _store = newStore;
            }
        }
        catch { /* ignore reload errors */ }
    }

    public IQueryable<IUser> Users => _store.Users.AsQueryable();
    public IQueryable<IApiKey> ApiKeys => _store.ApiKeys.AsQueryable();
    public IQueryable<IRequest> Requests => _store.Requests.AsQueryable();
    public IQueryable<IQuota> Quotas => _store.Quotas.AsQueryable();

    public Task SaveAsync<T>(T entity) where T : IEntity
    {
        if (entity is IUser user)
        {
            var existing = _store.Users.FirstOrDefault(u => u.Id == user.Id);
            if (existing != null)
                _store.Users.Remove(existing);
            if (user is User concreteUser)
                _store.Users.Add(concreteUser); // Cast to concrete type for serialization
            else
                throw new InvalidCastException("The provided IUser instance is not of type User.");
        }
        else if (entity is IApiKey apiKey)
        {
            var existing = _store.ApiKeys.FirstOrDefault(k => k.Key == apiKey.Key);
            if (existing != null)
                _store.ApiKeys.Remove(existing);
            if (apiKey is ApiKey concreteApiKey)
                _store.ApiKeys.Add(concreteApiKey); // Cast to concrete type for serialization
            else
                throw new InvalidCastException("The provided IApiKey instance is not of type ApiKey.");
        }
        else if (entity is IRequest request)
        {
            var existing = _store.Requests.FirstOrDefault(k => k.Key == request.Key);
            if (existing != null)
                _store.Requests.Remove(existing);
            if (request is Request concreteRequest)
                _store.Requests.Add(concreteRequest); // Cast to concrete type for serialization
            else
                throw new InvalidCastException("The provided IApiKey instance is not of type ApiKey.");
        }
        else if (entity is IQuota quota)
        {
            var existing = _store.Quotas.FirstOrDefault(q => q.Key == quota.Key && q.QuotaName == quota.QuotaName);
            if (existing != null)
                _store.Quotas.Remove(existing);
            if (quota is Quota concreteQuota)
                _store.Quotas.Add(concreteQuota);
            else
                throw new InvalidCastException("The provided IQuota instance is not of type Quota.");
        }
        else
        {
            throw new NotSupportedException($"Entity type {typeof(T).Name} not supported.");
        }
        SaveToFile();
        return Task.CompletedTask;
    }

    public Task UpsertAsync<T>(T entity, Expression<Func<T, bool>> filter) where T : IEntity
    {
        // For simplicity, just call SaveAsync (real implementation would use filter)
        return SaveAsync(entity);
    }

    public Task<QuotaConsumeResult> TryConsumeQuotaAsync(string quotaId, float amount)
    {
        lock (_syncRoot)
        {
            var quota = _store.Quotas.FirstOrDefault(q => q.Id == quotaId);
            if (quota == null)
                return Task.FromResult(QuotaConsumeResult.NotFound);

            if (quota.Type == "prepaid" && quota.Used + amount > quota.Limit)
                return Task.FromResult(QuotaConsumeResult.LimitExceeded);

            quota.Used += amount;
            SaveToFile();
            return Task.FromResult(QuotaConsumeResult.Consumed);
        }
    }

    public Task<bool> TryConsumeRateLimitAsync(string key, float rateLimitHz, float maxTokens, DateTime now)
    {
        lock (_syncRoot)
        {
            var request = _store.Requests
                .Where(r => r.Key == key)
                .OrderByDescending(r => r.LastUsed)
                .FirstOrDefault();
            var exists = request != null;

            if (!RateLimitTokenBucket.TryConsume(
                    exists,
                    request?.LastUsed ?? now,
                    request?.LastRate ?? 0f,
                    now,
                    rateLimitHz,
                    maxTokens,
                    out var remaining))
            {
                return Task.FromResult(false);
            }

            if (request != null)
            {
                request.LastUsed = now;
                request.LastRate = remaining;
            }
            else
            {
                _store.Requests.Add(new Request { Key = key, LastUsed = now, LastRate = remaining });
            }

            SaveToFile();
            return Task.FromResult(true);
        }
    }

    private void SaveToFile()
    {
        if (_watcher != null)
            _watcher.EnableRaisingEvents = false;
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_store, JsonOptions()));
        if (_watcher != null)
            _watcher.EnableRaisingEvents = true;
        // Manually trigger reload logic after saving to file
        ReloadFromFile();
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Internal data store for serialization
    private class DataStore
    {
        public List<User> Users { get; set; } = new();
        public List<ApiKey> ApiKeys { get; set; } = new();
        public List<Request> Requests { get; set; } = new();
        public List<Quota> Quotas { get; set; } = new();
    }

    // Concrete implementations for serialization
    public class User : IUser
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Email { get; set; } = "";
        public List<string> Permissions { get; set; } = new();
        // Explicit interface implementation for IUser.Permissions
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
        public DateTime LastUsed { get; set; } = DateTime.MinValue;
        public float LastRate { get; set; } = 0;
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
        public float Limit { get; set; } = 0;
        public string Type { get; set; } = "prepaid";
    }

    public IUser CreateUser() => new User();
    public IApiKey CreateApiKey() => new ApiKey();
    public IRequest CreateRequest() => new Request();
    public IQuota CreateQuota() => new Quota();
}
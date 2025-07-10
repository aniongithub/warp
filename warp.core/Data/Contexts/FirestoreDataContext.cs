using System.Linq.Expressions;
using FirebaseAdmin;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Grpc.Core;

namespace Warp.Core.Data.Contexts;

public class FirestoreDataContext : IDataContext
{
    private readonly FirestoreDb _db;

    public FirestoreDataContext(string projectId, string databaseName = "(default)")
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project ID is required for cloud environment.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("Database name cannot be null or empty.", nameof(databaseName));
        if (databaseName != "(default)")
            throw new ArgumentException("Database name must be '(default)', non-default databases are not supported", nameof(databaseName));

        try
        {
            // Check if running with Firestore emulator
            var emulatorHost = Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST");
            var isLocal = !string.IsNullOrEmpty(emulatorHost);

            if (isLocal)
            {
                Console.WriteLine($"Connecting to Firestore emulator at: {emulatorHost}");

                // For emulator, create a custom FirestoreClient that points to the emulator
                var clientBuilder = new FirestoreClientBuilder
                {
                    Endpoint = emulatorHost,
                    ChannelCredentials = ChannelCredentials.Insecure
                };
                var client = clientBuilder.Build();
                _db = FirestoreDb.Create(projectId, client);
                Console.WriteLine($"Firestore database connected to emulator successfully");
            }
            else
            {
                Console.WriteLine("Connecting to Firestore in production mode");

                // For cloud, try to use Google Cloud credentials
                try
                {
                    // Use Google.Cloud.Firestore directly which handles GCP authentication automatically
                    _db = FirestoreDb.Create(projectId);
                    Console.WriteLine($"Production Firestore database initialized successfully using default GCP credentials");
                }
                catch (Exception firestoreEx)
                {
                    Console.WriteLine($"Direct Firestore connection failed: {firestoreEx.Message}");
                    
                    // Fallback: Try Firebase Admin SDK with explicit credential handling
                    if (FirebaseApp.DefaultInstance == null)
                    {
                        var appOptions = new AppOptions()
                        {
                            ProjectId = projectId
                        };
                        
                        // Let Firebase Admin SDK use default credentials (service account key, metadata server, etc.)
                        var app = FirebaseApp.Create(appOptions);
                        Console.WriteLine($"Firebase Admin initialized for production project: {projectId}");
                    }

                    // Try again with Firebase Admin
                    _db = FirestoreDb.Create(projectId);
                    Console.WriteLine($"Production Firestore database initialized via Firebase Admin SDK");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize Firestore: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw new InvalidOperationException($"Failed to initialize Firestore database: {ex.Message}", ex);
        }
    }

    public IQueryable<IUser> Users => GetUsersAsync().Result.AsQueryable();
    public IQueryable<IApiKey> ApiKeys => GetApiKeysAsync().Result.AsQueryable();
    public IQueryable<IRequest> Requests => GetRequestsAsync().Result.AsQueryable();
    public IQueryable<IQuota> Quotas => GetQuotasAsync().Result.AsQueryable();

    private async Task<List<IUser>> GetUsersAsync()
    {
        var users = new List<IUser>();
        try
        {
            var snapshot = await _db.Collection("users").GetSnapshotAsync();
            
            foreach (var document in snapshot.Documents)
            {
                if (document.Exists)
                {
                    var data = document.ToDictionary();
                    users.Add(new User
                    {
                        Id = document.Id,
                        Email = data.GetValueOrDefault("email")?.ToString() ?? "",
                        Permissions = ParsePermissions(data.GetValueOrDefault("permissions")?.ToString())
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetUsersAsync: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        return users;
    }

    private async Task<List<IApiKey>> GetApiKeysAsync()
    {
        var keys = new List<IApiKey>();
        try
        {
            var snapshot = await _db.Collection("apikeys").GetSnapshotAsync();
            
            foreach (var document in snapshot.Documents)
            {
                if (document.Exists)
                {
                    var data = document.ToDictionary();
                    keys.Add(new ApiKey
                    {
                        Id = document.Id,
                        Key = data.GetValueOrDefault("key")?.ToString() ?? "",
                        Owner = data.GetValueOrDefault("owner")?.ToString() ?? "",
                        IsActive = Convert.ToBoolean(data.GetValueOrDefault("isActive") ?? true),
                        Permissions = ParsePermissions(data.GetValueOrDefault("permissions")?.ToString()),
                        RateLimitHz = Convert.ToSingle(data.GetValueOrDefault("rateLimitHz") ?? 1.0)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetApiKeysAsync: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        return keys;
    }

    private async Task<List<IRequest>> GetRequestsAsync()
    {
        var requests = new List<IRequest>();
        try
        {
            var snapshot = await _db.Collection("requests").GetSnapshotAsync();
            
            foreach (var document in snapshot.Documents)
            {
                if (document.Exists)
                {
                    var data = document.ToDictionary();
                    var lastUsedValue = data.GetValueOrDefault("lastUsed");
                    DateTime lastUsed = DateTime.MinValue;
                    
                    if (lastUsedValue is Timestamp timestamp)
                        lastUsed = timestamp.ToDateTime();
                    else if (lastUsedValue is string dateString && DateTime.TryParse(dateString, out var parsed))
                        lastUsed = parsed;

                    requests.Add(new Request
                    {
                        Id = document.Id,
                        Key = data.GetValueOrDefault("key")?.ToString() ?? "",
                        LastUsed = lastUsed,
                        LastRate = Convert.ToSingle(data.GetValueOrDefault("lastRate") ?? 0.0)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetRequestsAsync: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        return requests;
    }

    private async Task<List<IQuota>> GetQuotasAsync()
    {
        var quotas = new List<IQuota>();
        try
        {
            var snapshot = await _db.Collection("quotas").GetSnapshotAsync();
            
            foreach (var document in snapshot.Documents)
            {
                if (document.Exists)
                {
                    var data = document.ToDictionary();
                    quotas.Add(new Quota
                    {
                        Id = document.Id,
                        Key = data.GetValueOrDefault("key")?.ToString() ?? "",
                        QuotaName = data.GetValueOrDefault("quotaName")?.ToString() ?? "",
                        Used = Convert.ToSingle(data.GetValueOrDefault("used") ?? 0.0),
                        Limit = Convert.ToSingle(data.GetValueOrDefault("limit") ?? 0.0),
                        Type = data.GetValueOrDefault("type")?.ToString() ?? "prepaid"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GetQuotasAsync: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
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

    private async Task UpsertUserAsync(IUser user)
    {
        try
        {
            var docRef = _db.Collection("users").Document(user.Id);
            var data = new Dictionary<string, object>
            {
                { "email", user.Email },
                { "permissions", string.Join(",", user.Permissions ?? new List<string>()) }
            };
            await docRef.SetAsync(data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in UpsertUserAsync: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    private async Task UpsertApiKeyAsync(IApiKey apiKey)
    {
        try
        {
            var docRef = _db.Collection("apikeys").Document(apiKey.Id);
            var data = new Dictionary<string, object>
            {
                { "key", apiKey.Key },
                { "owner", apiKey.Owner },
                { "isActive", apiKey.IsActive },
                { "permissions", string.Join(",", apiKey.Permissions ?? new List<string>()) },
                { "rateLimitHz", apiKey.RateLimitHz }
            };
            await docRef.SetAsync(data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in UpsertApiKeyAsync: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    private async Task UpsertRequestAsync(IRequest request)
    {
        try
        {
            var docRef = _db.Collection("requests").Document(request.Id);
            var data = new Dictionary<string, object>
            {
                { "key", request.Key },
                { "lastUsed", Timestamp.FromDateTime(request.LastUsed.ToUniversalTime()) },
                { "lastRate", request.LastRate }
            };
            await docRef.SetAsync(data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in UpsertRequestAsync: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    private async Task UpsertQuotaAsync(IQuota quota)
    {
        try
        {
            var docRef = _db.Collection("quotas").Document(quota.Id);
            var data = new Dictionary<string, object>
            {
                { "key", quota.Key },
                { "quotaName", quota.QuotaName },
                { "used", quota.Used },
                { "limit", quota.Limit },
                { "type", quota.Type }
            };
            await docRef.SetAsync(data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in UpsertQuotaAsync: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    private static List<string> ParsePermissions(string? permissionsString)
    {
        return string.IsNullOrWhiteSpace(permissionsString) 
            ? new List<string>() 
            : permissionsString.Split(',').Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
    }

    // Concrete implementations for serialization (identical to other DataContexts)
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
        public float Limit { get; set; } = 0;
        public string Type { get; set; } = "prepaid";
    }

    public IUser CreateUser() => new User();
    public IApiKey CreateApiKey() => new ApiKey();
    public IRequest CreateRequest() => new Request();
    public IQuota CreateQuota() => new Quota();
}

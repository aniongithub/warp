using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Warp.Core.Data;

namespace Warp.Core.Job;

/// <summary>
/// Base implementation of IJobContext that provides common serialization functionality
/// </summary>
public abstract class JobContextBase : IJobContext
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null, // Use exact property names
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        
        // Add converters for interface types
        options.Converters.Add(new IUserJsonConverter());
        
        return options;
    }

    // Serialization implementation
    public virtual string SerializeJob<T>(T job) where T : class, IJob 
        => JsonSerializer.Serialize(job, _jsonOptions);

    public virtual T DeserializeJob<T>(string jobData) where T : class, IJob 
        => JsonSerializer.Deserialize<T>(jobData, _jsonOptions)!;

    // Abstract methods that concrete implementations must provide
    public abstract IJob CreateJob();
    public abstract Task EnqueueJobAsync<T>(T job) where T : class, IJob;
    public abstract Task<DequeueResult<T>> DequeueJobAsync<T>() where T : class, IJob;
    public abstract Task<T> LookupJobAsync<T>(string id, JobStatus status, string userId) where T : class, IJob;
    public abstract Task<JobStatus> GetJobStatusAsync(string id, string userId);
    public abstract Task<IAsyncEnumerable<T>> ListJobs<T>(string userId, JobStatus status, int batchSize) where T : class, IJob;
    public abstract Task UpdateJobAsync<T>(T job, JobStatus newStatus, string? error = null, string? output = null) where T : class, IJob;
}

/// <summary>
/// Custom converter to handle IUser interface deserialization
/// </summary>
public class IUserJsonConverter : JsonConverter<IUser>
{
    public override IUser? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Read the JSON object and deserialize as SqliteDataContext.User
        using var doc = JsonDocument.ParseValue(ref reader);
        var jsonText = doc.RootElement.GetRawText();
        return JsonSerializer.Deserialize<Warp.Core.Data.Contexts.SqliteDataContext.User>(jsonText);
    }

    public override void Write(Utf8JsonWriter writer, IUser value, JsonSerializerOptions options)
    {
        // Serialize as the concrete type
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}

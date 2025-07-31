using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Warp.Core.Data;
using Warp.Core.Data.JobContexts;
using Warp.Core.Middleware;

namespace Warp.Conduit.Middleware;

public class RedisJobDispatcherOptions : MiddlewareOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public int MaxConcurrentDispatches { get; set; } = 5;
    public int DispatchTimeoutMs { get; set; } = 30000;
    public Dictionary<string, InputMapping> Input { get; set; } = new();
    public string UserIdHeader { get; set; } = "X-JWT-Email";
}

public class InputMapping
{
    public InputSource From { get; set; } = new();
    public bool Required { get; set; } = false;
    public string? Default { get; set; }
}

public class InputSource
{
    public string? Header { get; set; }
    public string? Query { get; set; }
    public string? Body { get; set; }
}

public sealed class RedisJobDispatcher : MiddlewareBase<RedisJobDispatcherOptions>
{
    private readonly RedisJobContext _jobContext;

    public RedisJobDispatcher(string name, ILogger logger, IDataContext context, RedisJobDispatcherOptions options) 
        : base(name, logger, context, options)
    {
        _jobContext = new RedisJobContext(options.Channel, options.ConnectionString);
    }

    protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            // Extract input parameters based on configuration
            var extractedInputs = ExtractInputsAsync(context);
            
            var userEmail = context.Request.Headers[Options.UserIdHeader].FirstOrDefault();
            if (string.IsNullOrEmpty(userEmail))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync($"Missing user id header: {Options.UserIdHeader}");
                return;
            }
            var user = DataContext.Users.FirstOrDefault(u => u.Email == userEmail);
            if (user == null)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("User not found.");
                return;
            }

            // Create job (ID will be auto-generated)
            var job = _jobContext.CreateJob();
            job.Status = JobStatus.Queued;
            job.User = user;
            job.Input = JsonSerializer.Serialize(extractedInputs);
            job.QueuedAt = DateTime.UtcNow;

            // Enqueue the job
            await _jobContext.EnqueueJobAsync(job);

            // Return job ID as JSON response with 202 status
            context.Response.StatusCode = 202;
            context.Response.ContentType = "application/json";
            var response = new { jobId = job.Id };
            await context.Response.WriteAsync(JsonSerializer.Serialize(response));

            return; // Terminate pipeline
        }
        catch (ArgumentException ex)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync($"{{\"error\": \"{ex.Message}\"}}");
            return;
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"{{\"error\": \"Failed to dispatch job: {ex.Message}\"}}");
            return;
        }
    }

    private Dictionary<string, object?> ExtractInputsAsync(HttpContext context)
    {
        var extractedInputs = new Dictionary<string, object?>();

        foreach (var (paramName, mapping) in Options.Input)
        {
            object? value = null;

            // Extract from configured source
            if (!string.IsNullOrEmpty(mapping.From.Header))
                value = context.Request.Headers[mapping.From.Header].FirstOrDefault();
            else if (!string.IsNullOrEmpty(mapping.From.Query))
                value = context.Request.Query[mapping.From.Query].FirstOrDefault();
            else if (!string.IsNullOrEmpty(mapping.From.Body))
                // For body extraction, would need to parse request body
                // This is a simplified implementation
                throw new NotImplementedException("Body parameter extraction not yet implemented");

            // Apply default if no value found
            if (value == null || string.IsNullOrEmpty(value.ToString()))
                if (!string.IsNullOrEmpty(mapping.Default))
                    value = mapping.Default;
                else if (mapping.Required)
                    throw new ArgumentException($"Required parameter '{paramName}' is missing");

            extractedInputs[paramName] = value;
        }

        return extractedInputs;
    }
}

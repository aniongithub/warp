using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Warp.Core.Data;
using Warp.Core.Job;
using Warp.Core.Middleware;
using Warp.Dilithium.Transforms;
using Yarp.ReverseProxy.Model;

namespace Warp.Dilithium.Middleware;

public abstract class AsyncApiHandlerOptions : MiddlewareOptions
{
    public int MaxConcurrentDispatches { get; set; } = 5;
    public int DispatchTimeoutMs { get; set; } = 30000;
    public Dictionary<string, InputMapping> Input { get; set; } = new();
    public string UserIdHeader { get; set; } = "X-JWT-Email";
    public string Channel { get; set; } = string.Empty;
    
    /// <summary>
    /// Global blob transform configuration applied to all file uploads automatically
    /// </summary>
    public Warp.Core.Job.TransformConfig? BlobTransform { get; set; }
}

public class InputMapping
{
    public InputSource From { get; set; } = new();
    public bool Required { get; set; } = false;
    public string? Default { get; set; }
    public Warp.Core.Job.TransformConfig? Transform { get; set; }
}

public class InputSource
{
    public string? Header { get; set; }
    public string? Query { get; set; }
    public string? Body { get; set; }
}

public enum AsyncOperation
{
    Submit,
    Status,
    Result,
    Cancel
}

public class OperationContext
{
    public AsyncOperation Type { get; set; }
    public string? JobId { get; set; }
    public string RemainingPath { get; set; } = string.Empty;
}

public abstract class AsyncApiHandler<TOptions> : MiddlewareBase<TOptions>, IDisposable where TOptions : AsyncApiHandlerOptions
{
    private readonly SemaphoreSlim _concurrencyLimiter;
    private bool _disposed = false;

    protected AsyncApiHandler(string name, ILogger logger, IDataContext context, TOptions options) 
        : base(name, logger, context, options)
    {
        _concurrencyLimiter = new SemaphoreSlim(options.MaxConcurrentDispatches, options.MaxConcurrentDispatches);
    }

    protected override async Task<IResult> ProcessAsync(HttpContext context)
    {
        var operation = DetermineOperation(context.Request.Path, context.Request.Method);
        
        if (operation == null)
        {
            // Not an async API operation, pass through
            return Results.Ok().Continue();
        }

        try
        {
            switch (operation.Type)
            {
                case AsyncOperation.Submit:
                    return await HandleSubmit(context, operation);
                case AsyncOperation.Status:
                    return await HandleStatus(context, operation);
                case AsyncOperation.Result:
                    return await HandleResult(context, operation);
                case AsyncOperation.Cancel:
                    return await HandleCancel(context, operation);
                default:
                    return Results
                        .Problem(statusCode: 400, title: "Bad Request", detail: "Unknown async operation type")
                        .Stop();
            }
        }
        catch (ArgumentException ex)
        {
            return Results
                .Problem(statusCode: 400, title: "Bad Request", detail: ex.Message)
                .Stop();
        }
        catch (KeyNotFoundException ex)
        {
            return Results
                .Problem(statusCode: 404, title: "Not Found", detail: ex.Message)
                .Stop();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling async API operation");
            return Results
                .Problem(statusCode: 500, title: "Internal Server Error", detail: "Internal server error")
                .Stop();
        }
    }

    protected virtual OperationContext? DetermineOperation(string path, string method)
    {
        var operationType = GetOperationType(path, method);
        
        if (operationType == "Sync")
            return null; // Not an async operation
        
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        
        return operationType switch
        {
            "AsyncSubmit" => new OperationContext { Type = AsyncOperation.Submit },
            "AsyncStatus" => new OperationContext 
            { 
                Type = AsyncOperation.Status, 
                JobId = segments.Length > 0 ? segments[^1] : null 
            },
            "AsyncResult" => new OperationContext 
            { 
                Type = AsyncOperation.Result, 
                JobId = segments.Length > 0 ? segments[^1] : null 
            },
            "AsyncCancel" => new OperationContext 
            { 
                Type = AsyncOperation.Cancel, 
                JobId = segments.Length > 0 ? segments[^1] : null 
            },
            _ => null
        };
    }

    private async Task<IResult> HandleSubmit(HttpContext context, OperationContext operation)
    {
        // Check if we can acquire a slot for concurrent dispatch
        var timeoutMs = Options.DispatchTimeoutMs;
        Logger.LogDebug("Attempting to acquire concurrency slot (available: {Available}/{Max})", 
            _concurrencyLimiter.CurrentCount, Options.MaxConcurrentDispatches);
            
        var acquired = await _concurrencyLimiter.WaitAsync(timeoutMs);
        
        if (!acquired)
        {
            Logger.LogWarning("Rejected job submission due to max concurrent dispatches limit ({MaxConcurrent})", Options.MaxConcurrentDispatches);
            return Results
                .Problem(statusCode: 429, title: "Too Many Requests", detail: "Too many concurrent requests. Please try again later.")
                .Stop();
        }

        try
        {
            Logger.LogDebug("Concurrency slot acquired (remaining: {Remaining}/{Max})", 
                _concurrencyLimiter.CurrentCount, Options.MaxConcurrentDispatches);
                
            var extractedInputs = await ExtractInputsAsync(context);
            var parameterMappings = BuildParameterMappings(context, extractedInputs);
            var routingInfo = ExtractRoutingInfo(context);
            var user = await GetUserAsync(context);
            var jobId = await CreateAndEnqueueJobAsync(user, extractedInputs, parameterMappings, routingInfo);
            
            Logger.LogDebug("Job {JobId} submitted successfully, releasing concurrency slot", jobId);
            
            return Results
                .Json(new { jobId }, statusCode: 202)
                .Stop();
        }
        finally
        {
            _concurrencyLimiter.Release();
            Logger.LogDebug("Concurrency slot released (available: {Available}/{Max})", 
                _concurrencyLimiter.CurrentCount, Options.MaxConcurrentDispatches);
        }
    }

    private async Task<IResult> HandleStatus(HttpContext context, OperationContext operation)
    {
        if (string.IsNullOrEmpty(operation.JobId))
        {
            return Results
                .Problem(statusCode: 400, title: "Bad Request", detail: "Job ID is required for status operation")
                .Stop();
        }
        
        var user = await GetUserAsync(context);
        var status = await GetJobStatusAsync(operation.JobId, user.Id!);
        
        return Results
            .Json(new { jobId = operation.JobId, status = status.ToString() })
            .Stop();
    }

    private async Task<IResult> HandleResult(HttpContext context, OperationContext operation)
    {
        if (string.IsNullOrEmpty(operation.JobId))
        {
            return Results
                .Problem(statusCode: 400, title: "Bad Request", detail: "Job ID is required for result operation")
                .Stop();
        }
        
        var user = await GetUserAsync(context);
        var result = await GetJobResultAsync(operation.JobId, user.Id!);
        
        return Results
            .Json(result)
            .Stop();
    }

    private async Task<IResult> HandleCancel(HttpContext context, OperationContext operation)
    {
        if (string.IsNullOrEmpty(operation.JobId))
        {
            return Results
                .Problem(statusCode: 400, title: "Bad Request", detail: "Job ID is required for cancel operation")
                .Stop();
        }
        
        var user = await GetUserAsync(context);
        await CancelJobAsync(operation.JobId, user.Id!);
        
        return Results
            .NoContent()
            .Stop();
    }

    protected Task<IUser> GetUserAsync(HttpContext context)
    {
        var userEmail = context.Request.Headers[Options.UserIdHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(userEmail))
        {
            throw new ArgumentException($"Missing user id header: {Options.UserIdHeader}");
        }
        
        var user = DataContext.Users.FirstOrDefault(u => u.Email == userEmail);
        if (user == null)
        {
            throw new KeyNotFoundException($"User not found: {userEmail}");
        }
        
        return Task.FromResult(user);
    }

    // Abstract methods for concrete implementations
    protected abstract Task<string> CreateAndEnqueueJobAsync(IUser user, Dictionary<string, object?> extractedInputs, Dictionary<string, ParameterMapping> parameterMappings, JobRoutingInfo routingInfo);
    protected abstract Task<JobStatus> GetJobStatusAsync(string jobId, string userId);
    protected abstract Task<JobResult> GetJobResultAsync(string jobId, string userId);
    protected abstract Task CancelJobAsync(string jobId, string userId);

    // Transform helper methods
    private Dictionary<string, ParameterMapping> BuildParameterMappings(HttpContext context, Dictionary<string, object?> extractedInputs)
    {
        var mappings = new Dictionary<string, ParameterMapping>();
        
        // First, add explicitly configured mappings
        foreach (var (paramName, inputMapping) in Options.Input)
        {
            mappings[paramName] = new ParameterMapping
            {
                From = new Warp.Core.Job.InputSource
                {
                    Header = inputMapping.From.Header,
                    Query = inputMapping.From.Query,
                    Body = inputMapping.From.Body
                },
                Required = inputMapping.Required,
                Default = inputMapping.Default,
                Transform = inputMapping.Transform != null ? new Warp.Core.Job.TransformConfig
                {
                    Type = inputMapping.Transform.Type,
                    Options = inputMapping.Transform.Options
                } : null
            };
        }
        
        // Then, add mappings for any auto-extracted parameters that don't have explicit mappings
        foreach (var paramName in extractedInputs.Keys)
        {
            if (!mappings.ContainsKey(paramName))
            {
                // Determine the source based on where the parameter likely came from
                var source = new Warp.Core.Job.InputSource();
                
                if (paramName == "_httpMethod")
                {
                    // Special case: _httpMethod is internal metadata, no need to map back
                    source.Query = paramName;
                }
                else if (context.Request.Query.ContainsKey(paramName))
                {
                    // Parameter came from query string
                    source.Query = paramName;
                }
                else if (context.Request.Headers.ContainsKey(paramName))
                {
                    // Parameter came from headers
                    source.Header = paramName;
                }
                else
                {
                    // Default to body for other parameters (form data, JSON, etc.)
                    source.Body = paramName;
                }
                
                mappings[paramName] = new ParameterMapping
                {
                    From = source,
                    Required = false,
                    Default = null,
                    Transform = null
                };
                
                var sourceDescription = !string.IsNullOrEmpty(source.Header) ? $"Header:{source.Header}" :
                                       !string.IsNullOrEmpty(source.Query) ? $"Query:{source.Query}" :
                                       !string.IsNullOrEmpty(source.Body) ? $"Body:{source.Body}" : "Unknown";
                
                Logger.LogDebug("Created auto-mapping for parameter {ParamName} from {Source}", 
                    paramName, sourceDescription);
            }
        }
        
        return mappings;
    }

    private string GetSourceDescription(InputSource source)
    {
        if (!string.IsNullOrEmpty(source.Header))
            return $"Header:{source.Header}";
        if (!string.IsNullOrEmpty(source.Query))
            return $"Query:{source.Query}";
        if (!string.IsNullOrEmpty(source.Body))
            return $"Body:{source.Body}";
        return "Unknown";
    }

    // Routing information extraction (shared across all implementations)
    protected JobRoutingInfo ExtractRoutingInfo(HttpContext context)
    {
        // Extract routing information from YARP
        var proxyFeature = context.Features.Get<IReverseProxyFeature>();
        
        // Use the canonical path from context.Items["RequestPath"] instead of context.Request.Path
        var canonicalPath = context.Items["RequestPath"]?.ToString() ?? context.Request.Path.Value ?? "";
        
        // Remove /submit suffix to get the sync API path
        var originalPath = canonicalPath.EndsWith("/submit") 
            ? canonicalPath.Substring(0, canonicalPath.Length - "/submit".Length)
            : canonicalPath;
            
        var clusterId = proxyFeature?.Route?.Config?.ClusterId ?? "";
        
        // Don't set targetDestination to clusterId - let the job processor resolve it
        var targetDestination = "";
        
        // Collect relevant headers for the sync call
        var relevantHeaders = new Dictionary<string, string>();
        foreach (var header in context.Request.Headers)
        {
            // Include auth headers and other important ones
            if (header.Key.StartsWith("Authorization", StringComparison.OrdinalIgnoreCase) ||
                header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase))
            {
                relevantHeaders[header.Key] = header.Value.ToString();
            }
        }

        return new JobRoutingInfo
        {
            OriginalPath = originalPath,
            ClusterId = clusterId,
            TargetDestination = targetDestination,
            Headers = relevantHeaders
        };
    }

    // Input extraction logic with automatic extraction of all request data
    private async Task<Dictionary<string, object?>> ExtractInputsAsync(HttpContext context)
    {
        var extractedInputs = new Dictionary<string, object?>();

        // Preserve the original HTTP method for job processing
        extractedInputs["_httpMethod"] = context.Request.Method;

        // Auto-extract all query parameters
        foreach (var query in context.Request.Query)
        {
            extractedInputs[query.Key] = query.Value.FirstOrDefault();
        }

        // Auto-extract request body based on content type
        var contentType = context.Request.ContentType?.Split(';')[0]?.ToLowerInvariant();
        if (!string.IsNullOrEmpty(contentType))
        {
            context.Request.EnableBuffering();
            
            switch (contentType)
            {
                case "application/json":
                    await AutoExtractFromJsonBody(context, extractedInputs);
                    break;
                case "multipart/form-data":
                    await AutoExtractFromFormData(context, extractedInputs);
                    break;
                case "application/x-www-form-urlencoded":
                    await AutoExtractFromFormUrlEncoded(context, extractedInputs);
                    break;
            }
            
            // Reset body position for downstream middleware
            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
            }
        }

        // Apply explicit Input mappings as overrides/transformations
        foreach (var (paramName, mapping) in Options.Input)
        {
            object? value = null;

            // Extract from configured source (this can override auto-extracted values)
            if (!string.IsNullOrEmpty(mapping.From.Header))
            {
                value = context.Request.Headers[mapping.From.Header].FirstOrDefault();
            }
            else if (!string.IsNullOrEmpty(mapping.From.Query))
            {
                value = context.Request.Query[mapping.From.Query].FirstOrDefault();
            }
            else if (!string.IsNullOrEmpty(mapping.From.Body))
            {
                // For body fields, we might have already extracted them, so check first
                if (extractedInputs.TryGetValue(mapping.From.Body, out var existingValue))
                {
                    value = existingValue;
                }
                else
                {
                    // Fall back to manual extraction for specific field
                    switch (contentType)
                    {
                        case "application/json":
                            value = await ExtractFromJsonBody(context, mapping.From.Body);
                            break;
                        case "multipart/form-data":
                            value = await ExtractFromFormData(context, mapping.From.Body);
                            break;
                        case "application/x-www-form-urlencoded":
                            value = await ExtractFromFormUrlEncoded(context, mapping.From.Body);
                            break;
                    }
                }
            }
            else
            {
                // No specific source - use auto-extracted value if available
                extractedInputs.TryGetValue(paramName, out value);
            }

            // Apply default if no value found
            if (value == null || string.IsNullOrEmpty(value?.ToString()))
            {
                if (!string.IsNullOrEmpty(mapping.Default))
                    value = mapping.Default;
                else if (mapping.Required)
                    throw new ArgumentException($"Required parameter '{paramName}' is missing");
            }

            // Apply field-specific transform if configured
            if (mapping.Transform != null && value != null)
            {
                var transform = mapping.Transform.CreateTransform();
                var transformedValue = await transform.ForwardAsync(value);
                
                extractedInputs[paramName] = transformedValue;
                
                Logger.LogDebug("Applied transform {TransformType} to parameter {ParamName}: {OriginalValue} -> {TransformedValue}", 
                    mapping.Transform.Type, paramName, value, transformedValue);
            }
            else
            {
                extractedInputs[paramName] = value;
            }
        }

        return extractedInputs;
    }

    /// <summary>
    /// Automatically extract all fields from JSON body
    /// </summary>
    private async Task AutoExtractFromJsonBody(HttpContext context, Dictionary<string, object?> extractedInputs)
    {
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var json = await reader.ReadToEndAsync();
        
        if (string.IsNullOrEmpty(json))
            return;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            
            // Extract all top-level properties from JSON
            foreach (var property in root.EnumerateObject())
            {
                object? value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetDecimal(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText()
                };
                
                extractedInputs[property.Name] = value;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to auto-extract JSON body");
        }
    }

    /// <summary>
    /// Automatically extract all fields and files from multipart form data
    /// </summary>
    private async Task AutoExtractFromFormData(HttpContext context, Dictionary<string, object?> extractedInputs)
    {
        try
        {
            var form = await context.Request.ReadFormAsync();
            
            // Extract all form fields
            foreach (var field in form)
            {
                extractedInputs[field.Key] = field.Value.FirstOrDefault();
            }
            
            // Extract and transform all file uploads if BlobTransform is configured
            if (Options.BlobTransform != null)
            {
                var blobTransform = Options.BlobTransform!.CreateTransform();
                
                foreach (var file in form.Files)
                {
                    if (file.Length > 0)
                    {
                        Logger.LogDebug("Auto-transforming file upload: {FileName} (field: {FieldName})", 
                            file.FileName, file.Name);
                        
                        var transformedValue = await blobTransform.ForwardAsync(file);
                        extractedInputs[file.Name] = transformedValue;
                    }
                }
            }
            else
            {
                // No transform configured - just include file info
                foreach (var file in form.Files)
                {
                    if (file.Length > 0)
                    {
                        extractedInputs[file.Name] = new
                        {
                            FileName = file.FileName,
                            ContentType = file.ContentType,
                            Length = file.Length
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error auto-extracting multipart form data");
            throw new ArgumentException("Failed to process multipart form data", ex);
        }
    }

    /// <summary>
    /// Automatically extract all fields from form URL encoded data
    /// </summary>
    private async Task AutoExtractFromFormUrlEncoded(HttpContext context, Dictionary<string, object?> extractedInputs)
    {
        try
        {
            var form = await context.Request.ReadFormAsync();
            
            foreach (var field in form)
            {
                extractedInputs[field.Key] = field.Value.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to auto-extract form URL encoded data");
        }
    }

    private async Task<object?> ExtractFromJsonBody(HttpContext context, string fieldPath)
    {
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var json = await reader.ReadToEndAsync();
        
        if (string.IsNullOrEmpty(json))
            return null;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Support nested field paths like "user.name" or simple fields like "name"
        var pathParts = fieldPath.Split('.');
        var current = root;

        foreach (var part in pathParts)
        {
            if (current.TryGetProperty(part, out var property))
            {
                current = property;
            }
            else
            {
                return null; // Field not found
            }
        }

        // Return the appropriate type based on the JSON value
        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => current.GetRawText()
        };
    }

    private async Task<object?> ExtractFromFormData(HttpContext context, string fieldName)
    {
        var form = await context.Request.ReadFormAsync();
        
        // Check if it's a file field
        var file = form.Files.FirstOrDefault(f => f.Name == fieldName);
        if (file != null)
        {
            // Return the IFormFile directly - the transform will handle it
            return file;
        }
        
        // Check regular form fields
        if (form.ContainsKey(fieldName))
        {
            return form[fieldName].FirstOrDefault();
        }

        return null;
    }

    private async Task<object?> ExtractFromFormUrlEncoded(HttpContext context, string fieldName)
    {
        var form = await context.Request.ReadFormAsync();
        
        if (form.ContainsKey(fieldName))
        {
            return form[fieldName].FirstOrDefault();
        }

        return null;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _concurrencyLimiter?.Dispose();
            _disposed = true;
        }
    }
}

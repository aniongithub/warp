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
}

public class InputMapping
{
    public InputSource From { get; set; } = new();
    public bool Required { get; set; } = false;
    public string? Default { get; set; }
    public TransformConfig? Transform { get; set; }
}

public class TransformConfig
{
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, object?> Options { get; set; } = new();
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

public abstract class AsyncApiHandler<TOptions> : MiddlewareBase<TOptions> where TOptions : AsyncApiHandlerOptions
{
    protected AsyncApiHandler(string name, ILogger logger, IDataContext context, TOptions options) 
        : base(name, logger, context, options)
    {
    }

    protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var operation = DetermineOperation(context.Request.Path, context.Request.Method);
        
        if (operation == null)
        {
            // Not an async API operation, pass through
            await next(context);
            return;
        }

        try
        {
            switch (operation.Type)
            {
                case AsyncOperation.Submit:
                    await HandleSubmit(context, operation);
                    break;
                case AsyncOperation.Status:
                    await HandleStatus(context, operation);
                    break;
                case AsyncOperation.Result:
                    await HandleResult(context, operation);
                    break;
                case AsyncOperation.Cancel:
                    await HandleCancel(context, operation);
                    break;
            }
        }
        catch (ArgumentException ex)
        {
            await WriteJsonResponse(context, 400, new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            await WriteJsonResponse(context, 404, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling async API operation");
            await WriteJsonResponse(context, 500, new { error = "Internal server error" });
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

    private async Task HandleSubmit(HttpContext context, OperationContext operation)
    {
        var extractedInputs = await ExtractInputsAsync(context);
        var parameterMappings = BuildParameterMappings();
        var routingInfo = ExtractRoutingInfo(context);
        var user = await GetUserAsync(context);
        var jobId = await CreateAndEnqueueJobAsync(user, extractedInputs, parameterMappings, routingInfo);
        
        await WriteJsonResponse(context, 202, new { jobId });
    }

    private async Task HandleStatus(HttpContext context, OperationContext operation)
    {
        if (string.IsNullOrEmpty(operation.JobId))
        {
            throw new ArgumentException("Job ID is required for status operation");
        }
        
        var user = await GetUserAsync(context);
        var status = await GetJobStatusAsync(operation.JobId, user.Id!);
        
        await WriteJsonResponse(context, 200, new { jobId = operation.JobId, status = status.ToString() });
    }

    private async Task HandleResult(HttpContext context, OperationContext operation)
    {
        if (string.IsNullOrEmpty(operation.JobId))
        {
            throw new ArgumentException("Job ID is required for result operation");
        }
        
        var user = await GetUserAsync(context);
        var result = await GetJobResultAsync(operation.JobId, user.Id!);
        
        await WriteJsonResponse(context, 200, result);
    }

    private async Task HandleCancel(HttpContext context, OperationContext operation)
    {
        if (string.IsNullOrEmpty(operation.JobId))
        {
            throw new ArgumentException("Job ID is required for cancel operation");
        }
        
        var user = await GetUserAsync(context);
        await CancelJobAsync(operation.JobId, user.Id!);
        
        await WriteJsonResponse(context, 204, null);
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
            throw new ArgumentException("User not found");
        }
        
        return Task.FromResult(user);
    }

    protected async Task WriteJsonResponse(HttpContext context, int statusCode, object? data)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        
        if (data != null)
        {
            var json = JsonSerializer.Serialize(data);
            await context.Response.WriteAsync(json);
        }
    }

    // Abstract methods for concrete implementations
    protected abstract Task<string> CreateAndEnqueueJobAsync(IUser user, Dictionary<string, object?> extractedInputs, Dictionary<string, ParameterMapping> parameterMappings, JobRoutingInfo routingInfo);
    protected abstract Task<JobStatus> GetJobStatusAsync(string jobId, string userId);
    protected abstract Task<JobResult> GetJobResultAsync(string jobId, string userId);
    protected abstract Task CancelJobAsync(string jobId, string userId);

    // Transform helper methods
    private ITransform CreateTransform(TransformConfig transformConfig)
    {
        var transformType = Type.GetType(transformConfig.Type);
        if (transformType == null)
            throw new InvalidOperationException($"Transform type not found: {transformConfig.Type}");

        // Create options object - for now assume VolumeBlobTransformOptions
        // In a more complete implementation, we'd use reflection or a factory pattern
        if (transformConfig.Type.Contains("VolumeBlobTransform"))
        {
            var optionsType = typeof(VolumeBlobTransformOptions);
            var options = Activator.CreateInstance(optionsType) as VolumeBlobTransformOptions;
            
            if (transformConfig.Options.TryGetValue("VolumePath", out var volumePath))
                options!.VolumePath = volumePath?.ToString() ?? "uploads";
            
            return (ITransform)Activator.CreateInstance(transformType, options)!;
        }
        
        throw new NotSupportedException($"Transform type not supported: {transformConfig.Type}");
    }

    private Dictionary<string, ParameterMapping> BuildParameterMappings()
    {
        var mappings = new Dictionary<string, ParameterMapping>();
        
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

    // Input extraction logic with transform support
    private async Task<Dictionary<string, object?>> ExtractInputsAsync(HttpContext context)
    {
        var extractedInputs = new Dictionary<string, object?>();

        foreach (var (paramName, mapping) in Options.Input)
        {
            object? value = null;

            // Extract from configured source
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
                // Enable buffering to allow multiple reads of the request body
                context.Request.EnableBuffering();
                
                var contentType = context.Request.ContentType?.Split(';')[0]?.ToLowerInvariant();
                
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
                    default:
                        throw new NotSupportedException($"Content type '{context.Request.ContentType}' is not supported for body parameter extraction");
                }
                
                // Reset body position for downstream middleware
                if (context.Request.Body.CanSeek)
                {
                    context.Request.Body.Position = 0;
                }
            }

            // Apply default if no value found
            if (value == null || string.IsNullOrEmpty(value?.ToString()))
            {
                if (!string.IsNullOrEmpty(mapping.Default))
                    value = mapping.Default;
                else if (mapping.Required)
                    throw new ArgumentException($"Required parameter '{paramName}' is missing");
            }

            // Apply transform if configured
            if (mapping.Transform != null && value != null)
            {
                var transform = CreateTransform(mapping.Transform);
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
}

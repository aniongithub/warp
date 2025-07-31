using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Microsoft.OpenApi.Validations;
using Microsoft.OpenApi;
using System.Text.Json;

using Warp.Core.Data;
using Warp.Core.Middleware;

namespace Warp.Middleware;

public class OpenApiValidatorOptions : MiddlewareOptions
{
    public string SpecFile { get; set; } = "openapi.json";
}

public sealed class OpenApiValidator : MiddlewareBase<OpenApiValidatorOptions>
{
    private readonly OpenApiDocument _openApiDoc;

    public OpenApiValidator(string name, ILogger logger, IDataContext context, OpenApiValidatorOptions options)
        : base(name, logger, context, options)
    {
        using var stream = File.OpenRead(options.SpecFile);
        var reader = new OpenApiStreamReader();
        _openApiDoc = reader.Read(stream, out var diagnostic);
        if (diagnostic.Errors.Count > 0)
        {
            logger.LogWarning($"OpenAPI spec loaded with errors: {string.Join(", ", diagnostic.Errors.Select(e => e.Message))}");
        }
    }

    protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Do NOT set context.Items["RequestPath"] here. Only read it.
        var method = context.Request.Method.ToLowerInvariant();
        var path = context.GetRequestPath();

        // Find matching OpenAPI path
        var match = _openApiDoc.Paths.FirstOrDefault(p =>
            OpenApiPathMatch(p.Key, path));
        if (match.Key == null)
        {
            Logger.LogWarning($"No OpenAPI path matches request: {path}");
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync($"Path not found in OpenAPI spec: {path}");
            return;
        }

        // Check if method is allowed
        if (!match.Value.Operations.TryGetValue(ParseOperationType(method), out var operation))
        {
            Logger.LogWarning($"Method {method} not allowed for path {path} in OpenAPI spec");
            context.Response.StatusCode = 405;
            await context.Response.WriteAsync($"Method not allowed in OpenAPI spec: {method}");
            return;
        }

        // Validate parameters (query, header, path)
        var errors = new List<string>();
        foreach (var param in operation.Parameters)
        {
            var value = param.In switch
            {
                ParameterLocation.Query => context.Request.Query[param.Name].FirstOrDefault(),
                ParameterLocation.Header => context.Request.Headers[param.Name].FirstOrDefault(),
                ParameterLocation.Path => ExtractPathParameter(match.Key, path, param.Name),
                _ => null
            };
            if (param.Required && string.IsNullOrEmpty(value))
                errors.Add($"Missing required parameter: {param.Name}");
        }

        // Validate request body if present
        if (operation.RequestBody != null && operation.RequestBody.Content.Count > 0)
        {
            var contentType = context.Request.ContentType?.Split(';')[0];
            if (!operation.RequestBody.Content.ContainsKey(contentType))
            {
                errors.Add($"Unsupported content type: {contentType}");
            }
            else
            {
                // Optionally, validate body schema (basic check)
                var schema = operation.RequestBody.Content[contentType].Schema;
                if (schema != null && context.Request.ContentLength > 0)
                {
                    try
                    {
                        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                        var body = await reader.ReadToEndAsync();
                        context.Request.Body.Position = 0;
                        if (contentType == "application/json")
                        {
                            using var doc = JsonDocument.Parse(body);
                            // Optionally, add more schema validation here
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Invalid request body: {ex.Message}");
                    }
                }
            }
        }

        if (errors.Count > 0)
        {
            Logger.LogWarning($"OpenAPI validation failed: {string.Join(", ", errors)}");
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync($"OpenAPI validation failed: {string.Join(", ", errors)}");
            return;
        }

        await next(context);
    }

    private static bool OpenApiPathMatch(string openApiPath, string requestPath)
    {
        // Simple path matcher: treat {param} as wildcard
        var openApiSegments = openApiPath.Trim('/').Split('/');
        var requestSegments = requestPath.Trim('/').Split('/');
        if (openApiSegments.Length != requestSegments.Length)
            return false;
        for (int i = 0; i < openApiSegments.Length; i++)
        {
            if (openApiSegments[i].StartsWith("{")) continue;
            if (!string.Equals(openApiSegments[i], requestSegments[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static string? ExtractPathParameter(string openApiPath, string requestPath, string paramName)
    {
        var openApiSegments = openApiPath.Trim('/').Split('/');
        var requestSegments = requestPath.Trim('/').Split('/');
        for (int i = 0; i < openApiSegments.Length; i++)
        {
            if (openApiSegments[i].Equals($"{{{paramName}}}", StringComparison.OrdinalIgnoreCase))
                return requestSegments[i];
        }
        return null;
    }

    private static OperationType ParseOperationType(string method)
    {
        return method switch
        {
            "get" => OperationType.Get,
            "post" => OperationType.Post,
            "put" => OperationType.Put,
            "delete" => OperationType.Delete,
            "patch" => OperationType.Patch,
            "head" => OperationType.Head,
            "options" => OperationType.Options,
            "trace" => OperationType.Trace,
            _ => throw new NotSupportedException($"Unsupported HTTP method: {method}")
        };
    }
}

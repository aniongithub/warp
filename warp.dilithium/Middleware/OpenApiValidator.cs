using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Microsoft.OpenApi.Validations;
using Microsoft.OpenApi;
using System.Text.Json;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Warp.Core.Data;
using Warp.Core.Middleware;

namespace Warp.Dilithium.Middleware;

public class OpenApiValidatorOptions : MiddlewareOptions
{
    public string SpecFile { get; set; } = "openapi.json";
    public string? SpecUri { get; set; }
}

public sealed class OpenApiValidator : MiddlewareBase<OpenApiValidatorOptions>
{
    private readonly OpenApiDocument _openApiDoc;

    public OpenApiValidator(string name, ILogger logger, IDataContext context, OpenApiValidatorOptions options)
        : base(name, logger, context, options)
    {
        var reader = new OpenApiStreamReader();
        OpenApiDiagnostic diagnostic;

        if (!string.IsNullOrWhiteSpace(options.SpecUri))
        {
            using var http = new HttpClient();
            var bytes = http.GetByteArrayAsync(options.SpecUri).GetAwaiter().GetResult();
            using var stream = PrepareOpenApiStreamFromBytes(bytes);
            _openApiDoc = reader.Read(stream, out diagnostic);
        }
        else
        {
            var bytes = File.ReadAllBytes(options.SpecFile);
            using var stream = PrepareOpenApiStreamFromBytes(bytes);
            _openApiDoc = reader.Read(stream, out diagnostic);
        }
        if (diagnostic.Errors.Count > 0)
        {
            logger.LogWarning($"OpenAPI spec loaded with errors: {string.Join(", ", diagnostic.Errors.Select(e => e.Message))}");
        }
    }

    // HACK: OpenApiStreamReader doesn't support OpenAPI 3.1.x. If present, downgrade to 3.0.3 before parsing.
    private static MemoryStream PrepareOpenApiStreamFromBytes(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var jsonPattern = "\"openapi\"\\s*:\\s*\"3\\.1(\\.\\d+)?\"";
        var yamlPattern = "^\\s*openapi:\\s*3\\.1(\\.\\d+)?\\s*$";

        if (Regex.IsMatch(text, jsonPattern))
            text = Regex.Replace(text, jsonPattern, "\"openapi\": \"3.0.3\"");

        if (Regex.IsMatch(text, yamlPattern, RegexOptions.Multiline))
            text = Regex.Replace(text, yamlPattern, "openapi: 3.0.3", RegexOptions.Multiline);

        return new MemoryStream(Encoding.UTF8.GetBytes(text));
    }

    protected override async Task<IResult> ProcessAsync(HttpContext context)
    {
        // Get the final request path after all transforms have been applied
        var path = context.GetRequestPath();
        var method = context.Request.Method.ToLowerInvariant();

        // Ensure we handle /submit paths correctly for validation with OpenAPI spec that will not
        // have /submit suffixes on paths
        var operationType = GetOperationType(context.Request.Path, context.Request.Method);
        if (operationType == "AsyncSubmit")
        {
            var lastIndex = path.LastIndexOf("/submit", StringComparison.OrdinalIgnoreCase);
            if (lastIndex >= 0)
                path = path.Remove(lastIndex, "/submit".Length);
        }

        // Find matching OpenAPI path
        var match = _openApiDoc.Paths.FirstOrDefault(p =>
            OpenApiPathMatch(p.Key, path));
        if (match.Key == null)
        {
            Logger.LogWarning($"No OpenAPI path matches request: {path}");
            return Results
                .Problem(statusCode: 400, title: "Bad Request", detail: $"Request not found in OpenAPI spec: {path}")
                .Stop();
        }

        // Check if method is allowed
        if (!match.Value.Operations.TryGetValue(ParseOperationType(method), out var operation))
        {
            Logger.LogWarning($"Method {method} not allowed for path {path} in OpenAPI spec");
            return Results.
                Problem(statusCode: 405, title: "Method Not Allowed", detail: $"Method not allowed in OpenAPI spec: {method}")
                .Stop();
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
            if (string.IsNullOrEmpty(contentType) || !operation.RequestBody.Content.ContainsKey(contentType))
            {
                errors.Add($"Unsupported content type: {context.Request.ContentType}");
            }
            else
            {
                var schema = operation.RequestBody.Content[contentType].Schema;
                // Do not rely on ContentLength; it can be null for chunked uploads. Attempt to parse based on content type.
                if (schema != null)
                {
                    try
                    {
                        if (contentType == "application/json")
                        {
                            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                            var body = await reader.ReadToEndAsync();
                            context.Request.Body.Position = 0;
                            using var doc = JsonDocument.Parse(body);
                            // Optionally, add more schema validation here
                        }
                        else if (contentType == "multipart/form-data")
                        {
                            // Robust multipart parsing: enable buffering, parse sections, validate required fields/files
                            try { context.Request.EnableBuffering(); } catch { /* ignore if already enabled */ }

                            var mediaType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
                            var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
                            if (string.IsNullOrEmpty(boundary))
                            {
                                errors.Add("Missing multipart boundary.");
                            }
                            else
                            {
                                var seenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                                var mpReader = new MultipartReader(boundary, context.Request.Body);
                                MultipartSection? section;
                                while ((section = await mpReader.ReadNextSectionAsync()) != null)
                                {
                                    if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd))
                                    {
                                        var name = HeaderUtilities.RemoveQuotes(cd.Name).Value;
                                        if (!string.IsNullOrEmpty(name))
                                        {
                                            if (cd.FileName.HasValue || cd.FileNameStar.HasValue)
                                                seenFiles.Add(name);
                                            else
                                                seenFields.Add(name);
                                        }
                                    }
                                }

                                // Validate required fields from the OpenAPI schema
                                if (schema.Required != null)
                                {
                                    foreach (var requiredField in schema.Required)
                                    {
                                        var isFileProp = schema.Properties != null
                                            && schema.Properties.TryGetValue(requiredField, out var prop)
                                            && string.Equals(prop.Format, "binary", StringComparison.OrdinalIgnoreCase);

                                        if (isFileProp)
                                        {
                                            if (!seenFiles.Contains(requiredField))
                                                errors.Add($"Missing required file field: {requiredField}");
                                        }
                                        else
                                        {
                                            if (!seenFields.Contains(requiredField))
                                                errors.Add($"Missing required form field: {requiredField}");
                                        }
                                    }
                                }
                            }

                            // Rewind for downstream consumers
                            if (context.Request.Body.CanSeek)
                                context.Request.Body.Position = 0;
                        }
                        // For other content types, skip reading/parsing
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
            return Results
                .Problem(statusCode: 400, title: "Bad Request", detail: $"OpenAPI validation failed: {string.Join(", ", errors)}")
                .Stop();
        }

        return Results
            .Ok()
            .Continue();
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

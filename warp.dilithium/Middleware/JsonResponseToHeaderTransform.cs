using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Devlooped;

using Warp.Core.Data;
using Warp.Core.Middleware;

namespace Warp.Dilithium.Middleware
{
    public class JsonBodyMapping
    {
        public string JqSelector { get; set; } = "";
        public string DestinationHeader { get; set; } = "";
    }

    public class JsonBodyToHeaderTransformOptions : MiddlewareOptions
    {
        public List<JsonBodyMapping> Mappings { get; set; } = new();
    }

    public sealed class JsonResponseToHeaderTransform : MiddlewareBase<JsonBodyToHeaderTransformOptions>
    {
        public JsonResponseToHeaderTransform(string name, ILogger logger, IDataContext context, JsonBodyToHeaderTransformOptions options)
            : base(name, logger, context, options) { }

        protected override async Task<IResult> ProcessAsync(HttpContext context)
        {
            // Skip if no mappings are configured
            if (Options.Mappings.Count == 0)
            {
                return Results.Ok().Continue();
            }

            // Check if response has JSON content
            var contentType = context.Response.ContentType;
            if (string.IsNullOrEmpty(contentType) || !contentType.Contains("application/json"))
            {
                // Not JSON, continue to next middleware
                return Results.Ok().Continue();
            }

            // Read the response body that's already available
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            var responseBodyText = await new StreamReader(context.Response.Body, leaveOpen: true).ReadToEndAsync();
            context.Response.Body.Seek(0, SeekOrigin.Begin); // Reset for next middleware

            if (string.IsNullOrEmpty(responseBodyText))
            {
                // Empty response, nothing to process
                return Results.Ok().Continue();
            }

            try
            {
                // Apply each mapping
                foreach (var mapping in Options.Mappings)
                {
                    if (string.IsNullOrEmpty(mapping.JqSelector) || string.IsNullOrEmpty(mapping.DestinationHeader))
                        continue;

                    try
                    {
                        var value = await JQ.ExecuteAsync(responseBodyText, mapping.JqSelector);
                        if (!string.IsNullOrEmpty(value))
                        {
                            // Remove quotes from string values if present
                            if (value.StartsWith("\"") && value.EndsWith("\""))
                                value = value.Substring(1, value.Length - 2);

                            // Add header to response
                            context.Response.Headers[mapping.DestinationHeader] = value;
                            Logger.LogDebug("JsonResponseToHeaderTransform: Extracted '{Value}' from '{Selector}' to header '{Header}'", 
                                value, mapping.JqSelector, mapping.DestinationHeader);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "JsonResponseToHeaderTransform: Failed to extract value using selector '{Selector}'", 
                            mapping.JqSelector);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "JsonResponseToHeaderTransform: Failed to process JSON response body");
            }

            return Results.Ok().Continue();
        }
    }
}

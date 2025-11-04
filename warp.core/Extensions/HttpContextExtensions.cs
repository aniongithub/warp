using Microsoft.AspNetCore.Http;

namespace Warp.Core.Extensions;

public static class HttpContextExtensions
{
    /// <summary>
    /// Resolves a key from HTTP context headers based on a priority list of header names.
    /// This ensures consistent key resolution across different middleware and controllers.
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <param name="keyHeaders">List of header names to check in order of priority</param>
    /// <returns>The first non-empty header value found, or empty string if none found</returns>
    public static string ResolveKey(this HttpContext context, IEnumerable<string> keyHeaders)
    {
        var headers = new Dictionary<string, string>();
        foreach (var header in context.Request.Headers)
        {
            headers[header.Key] = header.Value.FirstOrDefault() ?? string.Empty;
        }
        return ResolveKey(headers, keyHeaders);
    }

    /// <summary>
    /// Resolves a key from a headers dictionary based on a priority list of header names.
    /// This ensures consistent key resolution logic across different contexts.
    /// </summary>
    /// <param name="headers">Dictionary of header names to values</param>
    /// <param name="keyHeaders">List of header names to check in order of priority</param>
    /// <returns>The first non-empty header value found, or empty string if none found</returns>
    public static string ResolveKey(Dictionary<string, string> headers, IEnumerable<string> keyHeaders)
    {
        if (keyHeaders != null && headers != null)
        {
            foreach (var header in keyHeaders)
            {
                if (!string.IsNullOrEmpty(header) && headers.TryGetValue(header, out var val))
                {
                    if (!string.IsNullOrEmpty(val))
                        return val;
                }
            }
        }
        return string.Empty;
    }
}
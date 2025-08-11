using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Warp.Dilithium.Middleware
{
    public static class HttpContextExtensions
    {
        public static string GetRequestPath(this HttpContext context)
        {
            if (context.Items.TryGetValue("RequestPath", out var value) && value is string s && !string.IsNullOrEmpty(s))
                return s;
            return context.Request.Path.Value ?? string.Empty;
        }
    }
}

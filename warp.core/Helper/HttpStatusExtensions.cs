namespace Warp.Core.Helper;

public static class HttpStatusExtensions
{
    public static bool IsErrorStatus(this int statusCode)
    {
        // 1xx, 2xx, and 3xx are considered successful or non-error responses
        // 4xx (client errors) and 5xx (server errors) are typically considered errors
        return statusCode >= 400;
    }

    public static string GetStatusDescription(this int statusCode)
    {
        return statusCode switch
        {
            200 => "OK",
            201 => "Created",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            429 => "Too Many Requests",
            500 => "Internal Server Error",
            _ => "Unknown Status"
        };
    }
}
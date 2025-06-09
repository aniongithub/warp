using Warp.Core.Data;

namespace Warp.Middleware;

public class PermissionsCheckerOptions : MiddlewareOptions
{
    public List<string> RequiredPermissions { get; set; } = new();
    public string UserIdHeader { get; set; } = "X-JWT-Email";
    public bool CreateUserIfNotFound { get; set; } = true;
    public List<string> DefaultPermissions { get; set; } = new();
    public string? RequiredPermissionsHeader { get; set; }
    public bool AddPermissionsHeader { get; set; } = true;
    public string PermissionsHeader { get; set; } = "X-Permissions";
}

public sealed class PermissionsChecker : MiddlewareBase<PermissionsCheckerOptions>
{
    public PermissionsChecker(string name, ILogger logger, IDataContext context, PermissionsCheckerOptions options)
        : base(name, logger, context, options) { }

    protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
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
            if (Options.CreateUserIfNotFound)
            {
                user = DataContext.CreateUser();
                user.Email = userEmail;
                if (Options.DefaultPermissions != null && Options.DefaultPermissions.Count > 0)
                    user.Permissions.AddRange(Options.DefaultPermissions);
                await DataContext.SaveAsync(user);
            }
            else
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("User not found.");
                return;
            }
        }

        List<string> requiredPermissions;
        if (Options.RequiredPermissions != null && Options.RequiredPermissions.Count > 0)
            requiredPermissions = Options.RequiredPermissions;
        else if (!string.IsNullOrEmpty(Options.RequiredPermissionsHeader) && context.Request.Headers.TryGetValue(Options.RequiredPermissionsHeader, out var headerVals))
            requiredPermissions = headerVals
                .Where(h => h != null)
                .SelectMany(h => (h ?? string.Empty).Split(","))
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
        else
            requiredPermissions = new List<string>();

        var missing = requiredPermissions.Except(user.Permissions ?? new List<string>()).ToList();
        if (missing.Count > 0)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync($"Missing permissions: {string.Join(",", missing)}");
            return;
        }

        // Optionally add X-Permissions header to request if all required permissions are present
        if (Options.AddPermissionsHeader)
        {
            var allPerms = user.Permissions != null ? string.Join(",", user.Permissions) : string.Empty;
            context.Request.Headers[Options.PermissionsHeader] = allPerms;
        }

        // Use context.GetRequestPath() if you need the path in this middleware

        await next(context);
    }
}
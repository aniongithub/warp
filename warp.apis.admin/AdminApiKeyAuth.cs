using System.Security.Cryptography;
using System.Text;

namespace Warp.Apis.Admin;

/// <summary>
/// Options controlling the Admin API authentication gate.
///
/// The Admin API exposes privileged user/permission/quota management endpoints. Historically it had
/// NO authentication of its own and relied purely on network isolation. This gate adds a simple,
/// config-driven shared-secret check that is ON by default and fails closed.
/// </summary>
public sealed class AdminAuthOptions
{
    /// <summary>
    /// When true (default), every request (except the health probe) must present the shared secret.
    /// Set to false ONLY for deployments that are genuinely isolated on a trusted network — this is
    /// the documented escape hatch and it logs a loud warning at startup.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Header the caller must send the shared secret in.</summary>
    public string HeaderName { get; set; } = "X-Admin-Api-Key";

    /// <summary>
    /// The shared secret / API key. Typically injected from the environment (e.g. ADMIN_API_KEY)
    /// via <c>${ADMIN_API_KEY:}</c> in configuration.
    /// </summary>
    public string? ApiKey { get; set; }
}

public static class AdminApiKeyAuth
{
    /// <summary>
    /// Wires the Admin API authentication gate into the pipeline. Reads the <c>AdminAuth</c> config
    /// section and falls back to the <c>ADMIN_API_KEY</c> environment variable for the secret.
    /// </summary>
    public static WebApplication UseAdminApiKeyAuth(this WebApplication app)
    {
        var options = new AdminAuthOptions();
        app.Configuration.GetSection("AdminAuth").Bind(options);

        // Allow the secret to come straight from the environment even if the config section is absent.
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            options.ApiKey = Environment.GetEnvironmentVariable("ADMIN_API_KEY");

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AdminApiKeyAuth");

        if (!options.Enabled)
        {
            logger.LogWarning(
                "SECURITY: Admin API authentication is DISABLED (AdminAuth:Enabled=false). All admin " +
                "endpoints are unauthenticated and rely entirely on network isolation. Only use this on a " +
                "fully trusted, isolated network.");
            return app;
        }

        var keyConfigured = !string.IsNullOrWhiteSpace(options.ApiKey);
        if (!keyConfigured)
        {
            logger.LogError(
                "SECURITY: Admin API authentication is enabled but no key is configured. Set ADMIN_API_KEY " +
                "(or AdminAuth:ApiKey), or explicitly set AdminAuth:Enabled=false for trusted-network " +
                "deployments. Until then, admin requests will be rejected with 503 (fail closed).");
        }

        var expectedKeyBytes = keyConfigured ? Encoding.UTF8.GetBytes(options.ApiKey!) : Array.Empty<byte>();

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            // Health probe stays open so orchestrators can check liveness without the secret.
            if (path.StartsWithSegments("/admin/health", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            if (!keyConfigured)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { error = "Admin API key is not configured." });
                return;
            }

            if (!context.Request.Headers.TryGetValue(options.HeaderName, out var provided) ||
                string.IsNullOrEmpty(provided.ToString()) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(provided.ToString()), expectedKeyBytes))
            {
                logger.LogWarning("Rejected admin request to {Path}: missing or invalid {Header}", path, options.HeaderName);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
                return;
            }

            await next(context);
        });

        logger.LogInformation("Admin API authentication gate enabled (header '{Header}').", options.HeaderName);
        return app;
    }
}

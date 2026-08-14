using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Warp.Apis.Developer;

/// <summary>
/// Options controlling which upstreams are trusted to inject identity headers.
///
/// The Developer API trusts <c>X-JWT-Email</c> and <c>X-Permissions</c> to identify the caller and
/// their permissions. That is only safe when the request genuinely comes from the Warp gateway, which
/// authenticates the JWT before forwarding. If the Developer API is reachable directly, a client could
/// spoof these headers and impersonate any user. This guard ensures the headers are only honored from a
/// trusted upstream.
/// </summary>
public sealed class GatewayTrustOptions
{
    /// <summary>
    /// When true (default), identity headers are stripped from any request that does not come from a
    /// trusted upstream (loopback, or carrying the shared-secret marker header). Set to false ONLY when
    /// the Developer API is unreachable except through the gateway — this escape hatch restores the old
    /// blind-trust behavior and logs a loud warning at startup.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Marker header the gateway includes to prove the request passed through it.</summary>
    public string HeaderName { get; set; } = "X-Gateway-Auth";

    /// <summary>
    /// Shared secret the gateway sends in <see cref="HeaderName"/>. Typically injected from the
    /// environment (e.g. GATEWAY_SHARED_SECRET) via <c>${GATEWAY_SHARED_SECRET:}</c> in configuration.
    /// When empty, only loopback requests are trusted.
    /// </summary>
    public string? SharedSecret { get; set; }

    /// <summary>Identity headers that are only honored from a trusted upstream.</summary>
    public List<string> ProtectedHeaders { get; set; } = new()
    {
        "X-JWT-Email", "X-Permissions", "X-JWT-Subject", "X-JWT-Audience", "X-JWT-Issuer"
    };
}

public static class GatewayHeaderTrust
{
    /// <summary>
    /// Wires the gateway-trust guard into the pipeline. Reads the <c>GatewayTrust</c> config section and
    /// falls back to the <c>GATEWAY_SHARED_SECRET</c> environment variable for the shared secret.
    /// </summary>
    public static WebApplication UseGatewayHeaderTrust(this WebApplication app)
    {
        var options = new GatewayTrustOptions();
        app.Configuration.GetSection("GatewayTrust").Bind(options);

        if (string.IsNullOrWhiteSpace(options.SharedSecret))
            options.SharedSecret = Environment.GetEnvironmentVariable("GATEWAY_SHARED_SECRET");

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayHeaderTrust");

        if (!options.Enabled)
        {
            logger.LogWarning(
                "SECURITY: Gateway header trust enforcement is DISABLED (GatewayTrust:Enabled=false). " +
                "The Developer API will blindly trust X-JWT-Email / X-Permissions from ANY caller. Only use " +
                "this when the Developer API is unreachable except through the gateway.");
            return app;
        }

        var hasSecret = !string.IsNullOrWhiteSpace(options.SharedSecret);
        if (!hasSecret)
        {
            logger.LogWarning(
                "Gateway header trust is enabled but no shared secret is configured (GATEWAY_SHARED_SECRET / " +
                "GatewayTrust:SharedSecret). Only loopback requests will be trusted to supply identity headers. " +
                "Set a shared secret and have the gateway send it in '{Header}' for cross-host deployments.",
                options.HeaderName);
        }

        var secretBytes = hasSecret ? Encoding.UTF8.GetBytes(options.SharedSecret!) : Array.Empty<byte>();

        app.Use(async (context, next) =>
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            var isLoopback = remoteIp != null && IPAddress.IsLoopback(remoteIp);

            var markerMatches = false;
            if (hasSecret &&
                context.Request.Headers.TryGetValue(options.HeaderName, out var marker) &&
                !string.IsNullOrEmpty(marker.ToString()))
            {
                markerMatches = CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(marker.ToString()), secretBytes);
            }

            var trusted = isLoopback || markerMatches;

            if (!trusted)
            {
                // Untrusted upstream: drop injected identity so endpoints treat the caller as anonymous
                // (they require X-JWT-Email and will return 401). This defeats header spoofing.
                foreach (var header in options.ProtectedHeaders)
                {
                    if (context.Request.Headers.Remove(header))
                        logger.LogWarning("Stripped untrusted identity header '{Header}' from request to {Path} (remote {Remote}).",
                            header, context.Request.Path, remoteIp?.ToString() ?? "unknown");
                }
            }

            // Never forward the marker further.
            context.Request.Headers.Remove(options.HeaderName);

            await next(context);
        });

        logger.LogInformation("Gateway header trust guard enabled (marker header '{Header}', loopback trusted).", options.HeaderName);
        return app;
    }
}

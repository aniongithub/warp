namespace Warp.Gateway;

/// <summary>
/// Options controlling the trust marker the gateway injects onto forwarded requests.
///
/// The downstream Developer API (see <c>GatewayHeaderTrust</c> in warp.apis.developer) only honors
/// identity headers (<c>X-JWT-Email</c>, <c>X-Permissions</c>, ...) when the request arrives over
/// loopback OR carries a matching shared-secret marker header. This is the gateway side of that
/// contract: it injects the marker so cross-host backends trust the identity the gateway established,
/// and strips any client-supplied identity/marker headers first so a client cannot spoof them.
///
/// Bound from the shared <c>GatewayTrust</c> configuration section (same section the Developer API
/// reads) so the header name, protected header list, and shared secret stay in sync across services.
/// </summary>
public sealed class GatewayAuthOptions
{
    /// <summary>When false, the gateway performs no header stripping or injection (opt-out escape hatch).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Marker header the gateway injects to prove the request passed through it.</summary>
    public string HeaderName { get; set; } = "X-Gateway-Auth";

    /// <summary>
    /// Shared secret sent in <see cref="HeaderName"/>. Resolved from <c>GatewayTrust:SharedSecret</c>
    /// and, failing that, the <c>GATEWAY_SHARED_SECRET</c> environment variable. When unset the whole
    /// injector is a no-op (a single startup warning is logged) so existing loopback/dev setups keep
    /// working unchanged.
    /// </summary>
    public string? SharedSecret { get; set; }

    /// <summary>
    /// Inbound client-supplied identity headers to strip before the gateway establishes identity, so a
    /// client cannot impersonate a user by pre-setting them. The gateway's own auth middleware
    /// (JwtValidator / ApiKeyValidator) re-adds the legitimate values from validated credentials.
    /// </summary>
    public List<string> ProtectedHeaders { get; set; } = new()
    {
        "X-JWT-Email", "X-Permissions", "X-JWT-Subject", "X-JWT-Audience", "X-JWT-Issuer"
    };
}

/// <summary>
/// Resolves <see cref="GatewayAuthOptions"/> from configuration and applies the gateway-side trust
/// marker (strip inbound spoofable headers, then inject the shared-secret marker) to forwarded requests.
/// </summary>
public static class GatewayAuthInjector
{
    /// <summary>
    /// Binds the <c>GatewayTrust</c> section and falls back to the <c>GATEWAY_SHARED_SECRET</c>
    /// environment variable for the shared secret.
    /// </summary>
    public static GatewayAuthOptions ResolveOptions(IConfiguration config)
    {
        var options = new GatewayAuthOptions();
        config.GetSection("GatewayTrust").Bind(options);

        if (string.IsNullOrWhiteSpace(options.SharedSecret))
            options.SharedSecret = Environment.GetEnvironmentVariable("GATEWAY_SHARED_SECRET");

        return options;
    }

    /// <summary>True when the injector is enabled and a shared secret is configured.</summary>
    public static bool IsActive(GatewayAuthOptions options)
        => options.Enabled && !string.IsNullOrWhiteSpace(options.SharedSecret);

    /// <summary>
    /// Applies the trust-marker handling to the current request: strips inbound client-supplied
    /// identity and marker headers, then injects the shared-secret marker so downstream trusted
    /// backends honor the identity this gateway sets (including across hosts). No-op when inactive.
    /// </summary>
    public static void Apply(HttpContext context, GatewayAuthOptions options)
    {
        if (!IsActive(options))
            return;

        // Defense-in-depth: drop anything the client may have supplied so it cannot spoof identity or
        // the marker. Legitimate identity headers are (re)added later by the auth middleware.
        foreach (var header in options.ProtectedHeaders)
            context.Request.Headers.Remove(header);
        context.Request.Headers.Remove(options.HeaderName);

        // Inject the marker so trusted backends accept the gateway-established identity cross-host.
        context.Request.Headers[options.HeaderName] = options.SharedSecret!;
    }
}

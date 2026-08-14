using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

using Warp.Core.Data;
using Warp.Core.Helper;
using Warp.Core.Middleware;

namespace Warp.Dilithium.Middleware;

public class JwtValidatorOptions: MiddlewareOptions
{
    public string HeaderName { get; set; } = "Authorization";
    public List<string> ValidAudiences { get; set; } = new();
    public List<string> ValidIssuers { get; set; } = new();
    public string AudienceWildcard { get; set; } = "*";
    public SecurityKey? SigningKey { get; set; }

    /// <summary>
    /// When true (the secure default), incoming JWTs MUST carry a valid signature that can be
    /// verified against the configured symmetric secret, <see cref="SigningKey"/>, or the keys
    /// discovered from <see cref="JwksUri"/>. If no verification material is available the
    /// middleware fails closed and rejects the request instead of trusting the token.
    /// </summary>
    public bool ValidateSigningKey { get; set; } = true;

    /// <summary>
    /// EXPLICIT, DANGEROUS opt-out for local/dev only. When true, signature validation is skipped
    /// entirely and ANY well-formed JWT is accepted. This must never be enabled in production.
    /// It is intentionally separate from <see cref="ValidateSigningKey"/> so that turning off
    /// signature checks is always a deliberate, auditable choice.
    /// </summary>
    public bool AllowUnsignedTokensInsecure { get; set; } = false;

    public string? Algorithm { get; set; } // Optional, for extensibility
    public List<string> ValidEmails { get; set; } = new();
    public string? JwksUri { get; set; } // JWKS endpoint for public key discovery

    /// <summary>
    /// How long (in seconds) JWKS signing keys are cached before a background refresh is attempted.
    /// The keys are cached in-process and reused across requests instead of being fetched per request.
    /// </summary>
    public int JwksCacheLifetimeSeconds { get; set; } = 3600;

    public bool CreateUserIfNotFound { get; set; } = true;
    public List<string> DefaultPermissions { get; set; } = new();
}

public sealed class JwtValidator : MiddlewareBase<JwtValidatorOptions>
{
    // A single shared HttpClient avoids socket exhaustion for JWKS fetches.
    private static readonly HttpClient _httpClient = new();

    private string _headerName = "Authorization";
    private List<string> _validAudiences = new();
    private List<string> _validIssuers = new();
    private string _audienceWildcard = "*";
    private SecurityKey? _signingKey;
    private bool _validateSigningKey = true;
    private bool _allowUnsignedTokensInsecure = false;
    private string? _algorithm;
    private List<string> _validEmails = new();
    private string? _jwksUri;

    // JWKS key cache (shared across all requests handled by this middleware instance).
    private readonly SemaphoreSlim _jwksLock = new(1, 1);
    private readonly TimeSpan _jwksCacheLifetime;
    private IList<SecurityKey>? _cachedJwksKeys;
    private DateTime _jwksCacheTimestampUtc = DateTime.MinValue;

    public JwtValidator(string name, ILogger logger, IDataContext context, JwtValidatorOptions options)
        : base(name, logger, context, options)
    {
        _headerName = options.HeaderName ?? "Authorization";
        _validAudiences = options.ValidAudiences ?? new();
        _validIssuers = options.ValidIssuers ?? new();
        _audienceWildcard = options.AudienceWildcard ?? "*";
        _signingKey = options.SigningKey;
        _validateSigningKey = options.ValidateSigningKey;
        _allowUnsignedTokensInsecure = options.AllowUnsignedTokensInsecure;
        _algorithm = options.Algorithm;
        _validEmails = options.ValidEmails ?? new();
        _jwksUri = options.JwksUri;
        _jwksCacheLifetime = TimeSpan.FromSeconds(options.JwksCacheLifetimeSeconds > 0 ? options.JwksCacheLifetimeSeconds : 3600);

        if (_allowUnsignedTokensInsecure)
        {
            Logger.LogWarning(
                "SECURITY: JwtValidator '{Name}' is running with AllowUnsignedTokensInsecure=true. " +
                "JWT signatures are NOT being verified and any well-formed token will be accepted. " +
                "This must only ever be used for local development.", Name);
        }
        else if (!_validateSigningKey)
        {
            // Legacy config compatibility: ValidateSigningKey=false no longer opens the gate on its own.
            // Signature verification stays required (fail closed); the operator must set the explicit
            // AllowUnsignedTokensInsecure flag to actually disable it.
            Logger.LogWarning(
                "JwtValidator '{Name}' has ValidateSigningKey=false, but signature validation now fails " +
                "closed by default. Tokens will still be signature-verified. To intentionally accept " +
                "unsigned tokens (dev only) set AllowUnsignedTokensInsecure=true.", Name);
        }

        Logger.LogDebug("JwtValidator configured with: HeaderName={HeaderName}, Audiences={Audiences}, Issuers={Issuers}, JWKS={JWKS}, ValidateSigningKey={ValidateSigningKey}, AllowUnsignedTokensInsecure={Insecure}", _headerName, string.Join(",", _validAudiences), string.Join(",", _validIssuers), _jwksUri, _validateSigningKey, _allowUnsignedTokensInsecure);
    }

    protected override async Task<IResult> ProcessAsync(HttpContext context)
    {
        Logger.LogDebug("Starting JWT validation for request: {Path}", context.Request.Path);
        if (!context.Request.Headers.TryGetValue(_headerName, out var tokenHeader))
        {
            Logger.LogWarning("Missing JWT token in header: {HeaderName}", _headerName);
            return Results
                .Problem(statusCode: 401, title: "Unauthorized", detail: "Missing JWT token.")
                .Stop();
        }

        var token = tokenHeader.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase).Trim();
        Logger.LogDebug("Extracted token: {Token}", token);
        var handler = new JwtSecurityTokenHandler();

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = _validIssuers.Count > 0,
            ValidIssuers = _validIssuers,
            ValidateAudience = _validAudiences.Count > 0 && !_validAudiences.Contains(_audienceWildcard),
            ValidAudiences = _validAudiences,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        if (_allowUnsignedTokensInsecure)
        {
            // Explicit, dangerous, dev-only opt-out. Skip signature verification entirely.
            Logger.LogWarning("SECURITY: Signature validation is disabled (AllowUnsignedTokensInsecure=true). Accepting token without verifying its signature.");
            validationParameters.ValidateIssuerSigningKey = false;
            validationParameters.RequireSignedTokens = false;
            validationParameters.SignatureValidator = (t, p) => new JwtSecurityToken(t);
        }
        else
        {
            // Secure default: fail closed. A verifiable signing key MUST be available.
            validationParameters.ValidateIssuerSigningKey = true;
            validationParameters.RequireSignedTokens = true;

            var hasKeyMaterial = false;

            if (_signingKey != null)
            {
                // Symmetric secret or explicitly-configured key. Do not weaken this path.
                validationParameters.IssuerSigningKey = _signingKey;
                hasKeyMaterial = true;
            }

            // Prefer JWKS discovery when no explicit key (or the key lacks a KeyId to match against).
            if (!string.IsNullOrEmpty(_jwksUri) && (_signingKey == null || string.IsNullOrEmpty(_signingKey.KeyId)))
            {
                try
                {
                    var keys = await GetCachedSigningKeysAsync(_jwksUri!);
                    if (keys.Count > 0)
                    {
                        validationParameters.IssuerSigningKeys = keys;
                        hasKeyMaterial = true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to obtain signing keys from JWKS URI: {JWKS}", _jwksUri);
                    return Results
                        .Problem(statusCode: 500, title: "Internal Server Error", detail: "Failed to fetch JWKS keys.")
                        .Stop();
                }
            }

            if (!hasKeyMaterial)
            {
                // No way to verify the signature and the operator has not explicitly opted into
                // insecure mode. Fail closed rather than silently trusting the token.
                Logger.LogError(
                    "SECURITY: JwtValidator '{Name}' cannot verify token signatures because no symmetric secret, " +
                    "signing key, or JWKS URI is configured, and AllowUnsignedTokensInsecure is false. Rejecting request.",
                    Name);
                return Results
                    .Problem(statusCode: 500, title: "Internal Server Error", detail: "JWT signing key is not configured.")
                    .Stop();
            }
        }

        try
        {
            var principal = handler.ValidateToken(token, validationParameters, out var validatedToken);
            Logger.LogInformation("JWT token validated successfully.");

            var jwtToken = validatedToken as JwtSecurityToken;
            if (jwtToken == null)
            {
                Logger.LogWarning("Validated token is not a JwtSecurityToken.");
                return Results
                    .Problem(statusCode: 401, title: "Unauthorized", detail: "Invalid JWT token.")
                    .Stop();
            }

            // Validate audience with wildcard support
            if (!_validAudiences.MatchesWildcard(jwtToken.Audiences.FirstOrDefault() ?? "", _audienceWildcard) && _validAudiences.Count > 0)
            {
                Logger.LogWarning("JWT audience validation failed. Token audiences: {Audiences}", string.Join(",", jwtToken.Audiences));
                return Results
                    .Problem(statusCode: 401, title: "Unauthorized", detail: "Invalid audience.")
                    .Stop();
            }

            // Add claims as headers
            context.Request.Headers["X-JWT-Subject"] = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            context.Request.Headers["X-JWT-Audience"] = string.Join(",", jwtToken.Audiences);
            context.Request.Headers["X-JWT-Issuer"] = jwtToken.Issuer;
            var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirst("email")?.Value ?? "";
            context.Request.Headers["X-JWT-Email"] = email;
            Logger.LogDebug("JWT claims added to headers: sub={Sub}, aud={Aud}, iss={Iss}, email={Email}", 
                context.Request.Headers["X-JWT-Subject"], context.Request.Headers["X-JWT-Audience"], 
                context.Request.Headers["X-JWT-Issuer"], email);

            // Wildcard email validation (using _validEmails)
            if (_validEmails.Count > 0)
            {
                if (!_validEmails.MatchesWildcard(email, "*"))
                {
                    Logger.LogWarning("JWT email validation failed. Email: {Email}", email);
                    return Results
                        .Problem(statusCode: 401, title: "Unauthorized", detail: "Invalid email.")
                        .Stop();
                }
                else
                {
                    Logger.LogDebug("JWT email validation succeeded. Email: {Email}", email);
                }
            }

            // Ensure user exists in DataContext
            var user = DataContext.Users.FirstOrDefault(u => u.Email == email);
            if (user == null)
            {
                if (Options.CreateUserIfNotFound)
                {
                    Logger.LogInformation("User with email {Email} not found. Creating new user.", email);
                    user = DataContext.CreateUser();
                    user.Email = email;
                    if (Options.DefaultPermissions != null && Options.DefaultPermissions.Count > 0)
                        user.Permissions.AddRange(Options.DefaultPermissions);
                    await DataContext.SaveAsync(user);
                }
                else
                {
                    Logger.LogWarning("User with email {Email} not found and CreateUserIfNotFound is false.", email);
                    return Results
                        .Problem(statusCode: 403, title: "Forbidden", detail: "User not found.")
                        .Stop();
                }
            }

            return Results
                .Ok()
                .Continue();
        }
        catch (SecurityTokenException ex)
        {
            Logger.LogWarning(ex, "JWT validation failed: {Message}", ex.Message);
            return Results
                .Problem(statusCode: 401, title: "Unauthorized", detail: $"JWT validation failed: {ex.Message}")
                .Stop();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error during JWT validation.");
            return Results
                .Problem(statusCode: 500, title: "Internal Server Error", detail: "Unexpected error during JWT validation.")
                .Stop();
        }
    }

    /// <summary>
    /// Returns the JWKS signing keys, using an in-process cache that is refreshed at most once per
    /// <see cref="JwtValidatorOptions.JwksCacheLifetimeSeconds"/>. If a refresh fails but previously
    /// cached keys exist, the stale keys are reused so a transient JWKS outage does not take down auth.
    /// </summary>
    private async Task<IList<SecurityKey>> GetCachedSigningKeysAsync(string jwksUri)
    {
        if (_cachedJwksKeys != null && DateTime.UtcNow - _jwksCacheTimestampUtc < _jwksCacheLifetime)
            return _cachedJwksKeys;

        await _jwksLock.WaitAsync();
        try
        {
            // Double-check after acquiring the lock in case another request just refreshed.
            if (_cachedJwksKeys != null && DateTime.UtcNow - _jwksCacheTimestampUtc < _jwksCacheLifetime)
                return _cachedJwksKeys;

            try
            {
                Logger.LogInformation("Refreshing signing keys from JWKS URI: {JWKS}", jwksUri);
                var keys = await GetSigningKeysFromJwksAsync(jwksUri);
                if (keys.Count > 0)
                {
                    _cachedJwksKeys = keys;
                    _jwksCacheTimestampUtc = DateTime.UtcNow;
                    Logger.LogInformation("Cached {KeyCount} signing keys from JWKS (refresh interval {Interval}s).", keys.Count, _jwksCacheLifetime.TotalSeconds);
                }
                else
                {
                    Logger.LogWarning("No signing keys found at JWKS URI: {JWKS}", jwksUri);
                }
            }
            catch (Exception ex) when (_cachedJwksKeys != null)
            {
                // Serve stale keys on refresh failure rather than failing auth entirely.
                Logger.LogWarning(ex, "Failed to refresh JWKS from {JWKS}; continuing to use cached keys.", jwksUri);
            }

            return _cachedJwksKeys ?? new List<SecurityKey>();
        }
        finally
        {
            _jwksLock.Release();
        }
    }

    private async Task<IList<SecurityKey>> GetSigningKeysFromJwksAsync(string jwksUri)
    {
        var jwksJson = await _httpClient.GetStringAsync(jwksUri);
        using var doc = JsonDocument.Parse(jwksJson);
        var keys = new List<SecurityKey>();
        if (doc.RootElement.TryGetProperty("keys", out var keysElement))
        {
            foreach (var keyElement in keysElement.EnumerateArray())
            {
                var kty = keyElement.GetProperty("kty").GetString();
                if (kty == "RSA")
                {
                    var n = keyElement.GetProperty("n").GetString();
                    var e = keyElement.GetProperty("e").GetString();
                    var key = new RsaSecurityKey(
                        new System.Security.Cryptography.RSAParameters
                        {
                            Modulus = Base64UrlEncoder.DecodeBytes(n),
                            Exponent = Base64UrlEncoder.DecodeBytes(e)
                        }
                    );
                    if (keyElement.TryGetProperty("kid", out var kidProp))
                        key.KeyId = kidProp.GetString();
                    keys.Add(key);
                }
                // Add support for EC keys if needed
            }
        }
        return keys;
    }
}
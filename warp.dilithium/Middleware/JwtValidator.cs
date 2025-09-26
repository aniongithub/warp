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
    public bool ValidateSigningKey { get; set; } = false;
    public string? Algorithm { get; set; } // Optional, for extensibility
    public List<string> ValidEmails { get; set; } = new();
    public string? JwksUri { get; set; } // JWKS endpoint for public key discovery

    public bool CreateUserIfNotFound { get; set; } = true;
    public List<string> DefaultPermissions { get; set; } = new();
}

public sealed class JwtValidator : MiddlewareBase<JwtValidatorOptions>
{
    private string _headerName = "Authorization";
    private List<string> _validAudiences = new();
    private List<string> _validIssuers = new();
    private string _audienceWildcard = "*";
    private SecurityKey? _signingKey;
    private bool _validateSigningKey = false;
    private string? _algorithm;
    private List<string> _validEmails = new();
    private string? _jwksUri;

    public JwtValidator(string name, ILogger logger, IDataContext context, JwtValidatorOptions options)
        : base(name, logger, context, options)
    {
        _headerName = options.HeaderName ?? "Authorization";
        _validAudiences = options.ValidAudiences ?? new();
        _validIssuers = options.ValidIssuers ?? new();
        _audienceWildcard = options.AudienceWildcard ?? "*";
        _signingKey = options.SigningKey;
        _validateSigningKey = options.ValidateSigningKey;
        _algorithm = options.Algorithm;
        _validEmails = options.ValidEmails ?? new();
        _jwksUri = options.JwksUri;

        Logger.LogDebug("JwtValidator configured with: HeaderName={HeaderName}, Audiences={Audiences}, Issuers={Issuers}, JWKS={JWKS}, ValidateSigningKey={ValidateSigningKey}", _headerName, string.Join(",", _validAudiences), string.Join(",", _validIssuers), _jwksUri, _validateSigningKey);
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
            ClockSkew = TimeSpan.FromMinutes(2),
            ValidateIssuerSigningKey = _validateSigningKey && _signingKey != null,
            IssuerSigningKey = _signingKey,
            RequireSignedTokens = _validateSigningKey && _signingKey != null
        };

        if (!_validateSigningKey)
        {
            Logger.LogInformation("Signature validation is disabled. Any JWT will be accepted.");
            validationParameters.SignatureValidator = (token, parameters) => new JwtSecurityToken(token);
        }

        if (_validateSigningKey && string.IsNullOrEmpty(_signingKey?.KeyId) && !string.IsNullOrEmpty(_jwksUri))
        {
            Logger.LogInformation("Fetching signing keys from JWKS URI: {JWKS}", _jwksUri);
            try
            {
                var keys = await GetSigningKeysFromJwksAsync(_jwksUri);
                if (keys.Count > 0)
                {
                    Logger.LogInformation("Fetched {KeyCount} signing keys from JWKS.", keys.Count);
                    validationParameters.IssuerSigningKeys = keys;
                    validationParameters.ValidateIssuerSigningKey = true;
                    validationParameters.RequireSignedTokens = true;
                }
                else
                {
                    Logger.LogWarning("No signing keys found at JWKS URI: {JWKS}", _jwksUri);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to fetch signing keys from JWKS URI: {JWKS}", _jwksUri);
                return Results
                    .Problem(statusCode: 500, title: "Internal Server Error", detail: "Failed to fetch JWKS keys.")
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

    private async Task<IList<SecurityKey>> GetSigningKeysFromJwksAsync(string jwksUri)
    {
        using var httpClient = new HttpClient();
        var jwksJson = await httpClient.GetStringAsync(jwksUri);
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
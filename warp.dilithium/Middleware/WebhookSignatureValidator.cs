using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Warp.Core.Data;
using Warp.Core.Middleware;
using System.Security.Cryptography;
using System.Text;

namespace Warp.Dilithium.Middleware;

public class WebhookSignatureValidatorOptions : MiddlewareOptions
{
    /// <summary>
    /// The header name containing the signature (e.g., "X-Hub-Signature-256", "X-Signature")
    /// </summary>
    public string SignatureHeader { get; set; } = "X-Hub-Signature-256";
    
    /// <summary>
    /// The secret key used to validate the signature. Can be overridden per route.
    /// </summary>
    public string? SecretKey { get; set; }
    
    /// <summary>
    /// The signature format. Supported: "sha256", "sha1", "md5"
    /// </summary>
    public string Algorithm { get; set; } = "sha256";
    
    /// <summary>
    /// The signature prefix (e.g., "sha256=", "sha1="). If empty, no prefix is expected.
    /// </summary>
    public string SignaturePrefix { get; set; } = "sha256=";
    
    /// <summary>
    /// Whether to allow requests with missing signatures (useful for testing)
    /// </summary>
    public bool AllowMissingSignature { get; set; } = false;
    
    /// <summary>
    /// Whether to validate the timestamp to prevent replay attacks
    /// </summary>
    public bool ValidateTimestamp { get; set; } = false;
    
    /// <summary>
    /// Timestamp header name (e.g., "X-Timestamp")
    /// </summary>
    public string TimestampHeader { get; set; } = "X-Timestamp";
    
    /// <summary>
    /// Maximum age of the timestamp in seconds (default: 5 minutes)
    /// </summary>
    public int MaxTimestampAge { get; set; } = 300;
    
    /// <summary>
    /// Whether to log the computed signature for debugging (security risk in production!)
    /// </summary>
    public bool LogSignatureForDebugging { get; set; } = false;
}

public sealed class WebhookSignatureValidator : MiddlewareBase<WebhookSignatureValidatorOptions>
{
    public WebhookSignatureValidator(string name, ILogger logger, IDataContext context, WebhookSignatureValidatorOptions options)
        : base(name, logger, context, options)
    {
        if (string.IsNullOrEmpty(options.SecretKey))
        {
            Logger.LogWarning("WebhookSignatureValidator '{Name}' has no SecretKey configured", name);
        }
    }

    protected override async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Check if we should apply to this request type
        if (!ShouldApplyToRequest(context))
        {
            await next(context);
            return;
        }

        var request = context.Request;
        
        // Get the signature from headers
        if (!request.Headers.TryGetValue(Options.SignatureHeader, out var signatureHeader))
        {
            if (Options.AllowMissingSignature)
            {
                Logger.LogWarning("Missing webhook signature header '{Header}' but allowing request", Options.SignatureHeader);
                await next(context);
                return;
            }
            
            Logger.LogWarning("Missing required webhook signature header: {Header}", Options.SignatureHeader);
            await RespondUnauthorized(context, "Missing webhook signature");
            return;
        }

        var receivedSignature = signatureHeader.ToString().Trim();
        if (string.IsNullOrEmpty(receivedSignature))
        {
            Logger.LogWarning("Empty webhook signature in header: {Header}", Options.SignatureHeader);
            await RespondUnauthorized(context, "Empty webhook signature");
            return;
        }

        // Validate timestamp if enabled
        if (Options.ValidateTimestamp)
        {
            if (!await ValidateTimestamp(context))
                return; // ValidateTimestamp already sent response
        }

        // Read the request body
        request.EnableBuffering(); // Allow multiple reads
        var body = await ReadRequestBodyAsync(request);
        
        // Get the secret key (could be from config, database, etc.)
        var secretKey = GetSecretKey(context);
        if (string.IsNullOrEmpty(secretKey))
        {
            Logger.LogError("No secret key available for webhook signature validation");
            await RespondUnauthorized(context, "Configuration error");
            return;
        }

        // Compute the expected signature
        var expectedSignature = ComputeSignature(body, secretKey);
        
        if (Options.LogSignatureForDebugging)
        {
            Logger.LogDebug("Received signature: {Received}, Expected: {Expected}", receivedSignature, expectedSignature);
        }

        // Compare signatures (constant-time comparison to prevent timing attacks)
        if (!IsValidSignature(receivedSignature, expectedSignature))
        {
            Logger.LogWarning("Invalid webhook signature. Expected format: {Format}", $"{Options.SignaturePrefix}<hash>");
            await RespondUnauthorized(context, "Invalid webhook signature");
            return;
        }

        Logger.LogDebug("Webhook signature validated successfully");
        
        // Reset body stream position for downstream middleware
        request.Body.Position = 0;
        
        await next(context);
    }

    private async Task<bool> ValidateTimestamp(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(Options.TimestampHeader, out var timestampHeader))
        {
            Logger.LogWarning("Missing required timestamp header: {Header}", Options.TimestampHeader);
            await RespondUnauthorized(context, "Missing timestamp");
            return false;
        }

        var timestampValue = timestampHeader.ToString().Trim();
        if (!long.TryParse(timestampValue, out var timestamp))
        {
            Logger.LogWarning("Invalid timestamp format: {Timestamp}", timestampValue);
            await RespondUnauthorized(context, "Invalid timestamp format");
            return false;
        }

        var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var age = Math.Abs(currentTimestamp - timestamp);
        
        if (age > Options.MaxTimestampAge)
        {
            Logger.LogWarning("Timestamp too old: {Age} seconds (max: {Max})", age, Options.MaxTimestampAge);
            await RespondUnauthorized(context, "Request timestamp too old");
            return false;
        }

        return true;
    }

    private async Task<string> ReadRequestBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0; // Reset for next read
        return body;
    }

    private string GetSecretKey(HttpContext context)
    {
        // Use the configured secret key (can be populated from environment via config system)
        if (!string.IsNullOrEmpty(Options.SecretKey))
            return Options.SecretKey;

        Logger.LogError("No secret key configured for webhook signature validation");
        return string.Empty;
    }

    private string ComputeSignature(string body, string secretKey)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secretKey);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        using HMAC hmac = Options.Algorithm.ToLowerInvariant() switch
        {
            "sha256" => new HMACSHA256(keyBytes),
            "sha1" => new HMACSHA1(keyBytes),
            "md5" => new HMACMD5(keyBytes),
            _ => throw new NotSupportedException($"Algorithm '{Options.Algorithm}' is not supported")
        };

        var hashBytes = hmac.ComputeHash(bodyBytes);
        var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        
        return string.IsNullOrEmpty(Options.SignaturePrefix) 
            ? hashHex 
            : $"{Options.SignaturePrefix}{hashHex}";
    }

    private bool IsValidSignature(string received, string expected)
    {
        // Constant-time comparison to prevent timing attacks
        if (received.Length != expected.Length)
            return false;

        var result = 0;
        for (var i = 0; i < received.Length; i++)
        {
            result |= received[i] ^ expected[i];
        }
        
        return result == 0;
    }

    private async Task RespondUnauthorized(HttpContext context, string message)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            
            var response = new
            {
                error = "Unauthorized",
                message = message,
                timestamp = DateTimeOffset.UtcNow.ToString("O")
            };
            
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(response));
        }
    }
}

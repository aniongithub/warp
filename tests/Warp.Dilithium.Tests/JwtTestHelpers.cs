using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Warp.Core.Data.Contexts;
using Warp.Core.Middleware;
using Warp.Dilithium.Middleware;

namespace Warp.Dilithium.Tests;

/// <summary>
/// Shared plumbing for exercising the sealed <see cref="JwtValidator"/>. Its decision logic lives in
/// the protected <c>ProcessAsync</c>, so it is invoked by reflection; the returned
/// <see cref="Result"/> is inspected for its pipeline action and, when it stops the pipeline, executed
/// against the response to surface the HTTP status code.
/// </summary>
internal static class JwtTestHelpers
{
    public const string Email = "user@example.com";

    public static SymmetricSecurityKey SymmetricKey(string seed = "warp-super-secret-signing-key-0123456789")
        => new(System.Text.Encoding.UTF8.GetBytes(seed));

    public static string SignHs256(SymmetricSecurityKey key, DateTime? expires = null, string email = Email)
    {
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var exp = expires ?? DateTime.UtcNow.AddMinutes(10);
        var token = new JwtSecurityToken(
            claims: Claims(email),
            notBefore: exp.AddMinutes(-30), // always before expiry, even for a deliberately-expired token
            expires: exp,
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string Unsigned(string email = Email)
    {
        // No signing credentials => "alg": "none".
        var token = new JwtSecurityToken(claims: Claims(email), expires: DateTime.UtcNow.AddMinutes(10));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static Claim[] Claims(string email) => new[]
    {
        new Claim(ClaimTypes.NameIdentifier, "subject-1"),
        new Claim(JwtRegisteredClaimNames.Email, email),
    };

    public static JwtValidator NewValidator(JwtValidatorOptions options, out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), "warp-jwt-" + Guid.NewGuid().ToString("N") + ".json");
        var dataContext = new JsonDataContext(dbPath);
        return new JwtValidator("jwt", NullLogger.Instance, dataContext, options);
    }

    public static DefaultHttpContext ContextWithBearer(string token)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Authorization"] = "Bearer " + token;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    public static async Task<Result> RunAsync(JwtValidator validator, HttpContext context)
    {
        var method = typeof(JwtValidator).GetMethod("ProcessAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<IResult>)method.Invoke(validator, new object[] { context })!;
        var result = await task;
        return result.Should().BeOfType<Result>().Subject;
    }

    /// <summary>Reads the HTTP status code a rejecting result would write, without needing DI plumbing.</summary>
    public static int StatusCodeOf(Result result)
    {
        result.Action.Should().Be(PipelineAction.Stop, "only a rejecting result carries a status code");
        var inner = result.InnerResult as IStatusCodeHttpResult;
        inner.Should().NotBeNull("the rejecting result should expose a status code");
        return inner!.StatusCode ?? 0;
    }
}

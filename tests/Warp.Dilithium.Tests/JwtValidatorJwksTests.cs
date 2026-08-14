using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Warp.Core.Middleware;
using Warp.Dilithium.Middleware;

namespace Warp.Dilithium.Tests;

/// <summary>
/// A tiny in-process JWKS endpoint backed by <see cref="HttpListener"/>. It counts requests and can
/// be flipped into a failure mode to exercise the validator's stale-serve fallback.
/// </summary>
internal sealed class JwksTestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _jwksJson;

    public string Url { get; }
    public int RequestCount;
    public volatile bool FailRequests;

    public JwksTestServer(RSA rsa, string kid)
    {
        var p = rsa.ExportParameters(false);
        var n = Base64UrlEncoder.Encode(p.Modulus);
        var e = Base64UrlEncoder.Encode(p.Exponent);
        _jwksJson = $"{{\"keys\":[{{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"{kid}\",\"n\":\"{n}\",\"e\":\"{e}\"}}]}}";

        var port = FreePort();
        Url = $"http://127.0.0.1:{port}/jwks/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            Interlocked.Increment(ref RequestCount);
            try
            {
                if (FailRequests)
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                }
                else
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(_jwksJson);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            }
            catch { /* client went away */ }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}

/// <summary>
/// Coverage for JWKS-based signing-key discovery: keys are fetched once and cached across requests
/// (cache hit, no re-fetch), and on a later refresh failure the previously cached keys are still
/// served so a transient JWKS outage does not take down authentication.
/// </summary>
public class JwtValidatorJwksTests
{
    private const string Kid = "warp-test-kid";

    private static string SignRsa(RSA rsa)
    {
        var key = new RsaSecurityKey(rsa) { KeyId = Kid };
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            claims: JwtTestHelpers.Claims(JwtTestHelpers.Email),
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task Jwks_Keys_AreCached_AcrossRequests()
    {
        using var rsa = RSA.Create(2048);
        using var server = new JwksTestServer(rsa, Kid);
        var options = new JwtValidatorOptions { JwksUri = server.Url, JwksCacheLifetimeSeconds = 3600 };
        var validator = JwtTestHelpers.NewValidator(options, out _);

        var first = await JwtTestHelpers.RunAsync(validator, JwtTestHelpers.ContextWithBearer(SignRsa(rsa)));
        var second = await JwtTestHelpers.RunAsync(validator, JwtTestHelpers.ContextWithBearer(SignRsa(rsa)));

        first.Action.Should().Be(PipelineAction.Continue);
        second.Action.Should().Be(PipelineAction.Continue);
        server.RequestCount.Should().Be(1, "the second validation must be served from the in-process JWKS cache");
    }

    [Fact]
    public async Task Jwks_ServesStaleKeys_WhenRefreshFails()
    {
        using var rsa = RSA.Create(2048);
        using var server = new JwksTestServer(rsa, Kid);
        var options = new JwtValidatorOptions { JwksUri = server.Url, JwksCacheLifetimeSeconds = 3600 };
        var validator = JwtTestHelpers.NewValidator(options, out _);

        // Prime the cache.
        (await JwtTestHelpers.RunAsync(validator, JwtTestHelpers.ContextWithBearer(SignRsa(rsa)))).Action
            .Should().Be(PipelineAction.Continue);
        server.RequestCount.Should().Be(1);

        // Force the cache to look expired, and make the JWKS endpoint start failing.
        ForceCacheExpiry(validator);
        server.FailRequests = true;

        var afterOutage = await JwtTestHelpers.RunAsync(validator, JwtTestHelpers.ContextWithBearer(SignRsa(rsa)));

        afterOutage.Action.Should().Be(PipelineAction.Continue, "stale keys must be served when a refresh fails");
        server.RequestCount.Should().Be(2, "a refresh should have been attempted before falling back to stale keys");
    }

    private static void ForceCacheExpiry(JwtValidator validator)
    {
        var field = typeof(JwtValidator).GetField("_jwksCacheTimestampUtc",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(validator, DateTime.MinValue);
    }
}

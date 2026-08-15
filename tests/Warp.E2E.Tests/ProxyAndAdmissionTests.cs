using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Warp.E2E.Tests;

/// <summary>
/// Scenarios 1, 3, 4 and 5: synchronous proxy, prepaid quota admission control, rate limiting,
/// and the auth/identity boundary (including the gateway trust-marker inject/strip contract).
/// </summary>
[Trait("Category", "E2E")]
[Collection("e2e")]
public class ProxyAndAdmissionTests
{
    private readonly E2EStack _stack;

    public ProxyAndAdmissionTests(E2EStack stack) => _stack = stack;

    // --- Scenario 1: synchronous proxy returns the stubbed upstream 200 body ---
    [Fact]
    public async Task Sync_proxy_returns_upstream_response()
    {
        using var client = _stack.NewGatewayClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/echo/hello-e2e");
        req.Headers.Add("X-JWT-Email", $"sync-{Guid.NewGuid():N}@e2e.test");

        using var res = await client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // The echo upstream reflects the forwarded request; the transform strips the /echo prefix.
        doc.RootElement.GetProperty("path").GetString().Should().Contain("hello-e2e");
    }

    // --- Scenario 3: prepaid quota is enforced at admission; a rejected request never dispatches ---
    [Fact]
    public async Task Quota_exhaustion_returns_429_at_admission_without_dispatch()
    {
        using var client = _stack.NewGatewayClient();
        var email = $"quota-{Guid.NewGuid():N}@e2e.test";

        // QuotaLimit=3, ReserveAmount=1 -> the first three admit, the fourth is rejected.
        for (var i = 0; i < 3; i++)
        {
            using var ok = await Get(client, "/quota/item", email);
            ok.StatusCode.Should().Be(HttpStatusCode.OK, $"request {i + 1} is within the prepaid quota");
        }

        using var rejected = await Get(client, "/quota/item", email);
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var body = await rejected.Content.ReadAsStringAsync();
        // The QuotaChecker short-circuits (Preprocess) so the echo upstream is never reached: the
        // body is the quota problem-detail, not the echo reflection.
        body.Should().Contain("Quota");
        body.Should().NotContain("\"method\"", "a rejected request must not have been dispatched to the echo upstream");
    }

    // --- Scenario 4: bursting past RateLimitHz returns 429 ---
    [Fact]
    public async Task Rate_limit_burst_returns_429()
    {
        using var client = _stack.NewGatewayClient();
        var apiKey = $"rl-{Guid.NewGuid():N}";

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 15; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/rl/ping");
            req.Headers.Add("X-Api-Key", apiKey);
            using var res = await client.SendAsync(req);
            statuses.Add(res.StatusCode);
        }

        // RateLimitHz=3 -> a 15-request burst must trip at least one 429.
        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
    }

    // --- Scenario 5a: the identity boundary is enforced on the gateway ---
    [Fact]
    public async Task Missing_identity_is_rejected_and_valid_identity_is_accepted()
    {
        using var client = _stack.NewGatewayClient();

        using var anon = await client.GetAsync("/echo/secure");
        anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var authed = await Get(client, "/echo/secure", $"auth-{Guid.NewGuid():N}@e2e.test");
        authed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- Scenario 5b: the gateway strips spoofed identity/marker headers and injects X-Gateway-Auth ---
    [Fact]
    public async Task Trust_gateway_injects_marker_and_strips_spoofed_headers()
    {
        using var client = _stack.NewTrustGatewayClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/trust/echo-back");
        req.Headers.Add("X-JWT-Email", "attacker@evil.test");   // spoofed identity
        req.Headers.Add("X-Gateway-Auth", "bogus-marker");       // spoofed marker

        using var res = await client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var headers = doc.RootElement.GetProperty("headers");

        // The gateway injected its real shared secret, overriding the client's spoofed value...
        HeaderValue(headers, "x-gateway-auth").Should().Be(E2EStack.GatewaySharedSecret);
        // ...and stripped the spoofed identity header before forwarding upstream.
        HasHeader(headers, "x-jwt-email").Should().BeFalse(
            "the gateway strips client-supplied identity headers so they cannot be spoofed");
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string path, string email)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("X-JWT-Email", email);
        return client.SendAsync(req);
    }

    private static string? HeaderValue(JsonElement headers, string name)
    {
        foreach (var p in headers.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.Array
                    ? p.Value[0].GetString()
                    : p.Value.GetString();
        return null;
    }

    private static bool HasHeader(JsonElement headers, string name)
    {
        foreach (var p in headers.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

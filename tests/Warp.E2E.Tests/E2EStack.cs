using System.Diagnostics;
using System.Net.Http;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Testcontainers.PostgreSql;
using Warp.Core.Data;
using Warp.Core.Data.Contexts;
using Warp.Core.Job;
using Warp.Core.Job.Contexts;

namespace Warp.E2E.Tests;

/// <summary>
/// Brings up the full Warp stack for the end-to-end suite on a single Docker network:
/// postgres (shared state), redis (async queue), localstripe (pinned digest), a stub echo
/// upstream, and the warp gateway / plasma / latinum processes built from freshly published
/// binaries plus a dedicated e2e config.
///
/// The image is built programmatically via the Docker socket (no docker CLI / compose needed
/// at test time) from <c>compose/Dockerfile</c>, which layers the published output + config on
/// the ASP.NET runtime. This mirrors what the CI e2e job and <c>docker-compose.e2e.yml</c> do
/// but keeps the in-container run self-contained.
/// </summary>
public sealed class E2EStack : IAsyncLifetime
{
    // Pinned, anonymously-pullable localstripe (multi-arch index digest; runner auto-selects arch).
    public const string LocalStripeImage =
        "docker.io/aniondocker/localstripe:1.15.11@sha256:444c2c7724e4c1e33c1a3e969c5a47487776efa78d88e1b4518390c7a9620bd6";

    private const string EchoImage = "mendhak/http-https-echo:31";
    private const string RedisImage = "redis:7-alpine";

    // Known signing secret the tests sign webhooks with; latinum verifies against the same value.
    public const string WebhookSecret = "whsec_e2e_0123456789abcdef0123456789abcdef";
    public const string GatewaySharedSecret = "e2e-gateway-secret-abc123";

    private const string PgDb = "warp";
    private const string PgUser = "warp";
    private const string PgPass = "warp_e2e";
    private const string PgConnInternal = "Host=postgres;Port=5432;Database=warp;Username=warp;Password=warp_e2e";
    private const string RedisConn = "redis:6379,defaultDatabase=0";
    private const string StripeApiBaseInternal = "http://localstripe:8420";
    private const string EchoAddrInternal = "http://echo:8080";

    private INetwork _network = null!;
    private PostgreSqlContainer _postgres = null!;
    private IContainer _redis = null!;
    private IContainer _localstripe = null!;
    private IContainer _echo = null!;
    private IContainer _gateway = null!;
    private IContainer _trustGateway = null!;
    private IContainer _plasma = null!;
    private IContainer _latinum = null!;
    private IFutureDockerImage _image = null!;
    private string? _buildContext;

    /// <summary>Base URL for the warp gateway (host-mapped :5000).</summary>
    public string GatewayBaseUrl { get; private set; } = null!;

    /// <summary>
    /// Base URL for a second gateway that has GATEWAY_SHARED_SECRET set, so it strips client
    /// identity/marker headers and injects X-Gateway-Auth. The main gateway leaves the dev-header
    /// identity model intact (the injector runs before PermissionsChecker, so enabling it there
    /// would strip X-JWT-Email before identity is established).
    /// </summary>
    public string TrustGatewayBaseUrl { get; private set; } = null!;

    /// <summary>Base URL for warp.latinum's webhook endpoint (host-mapped :5004).</summary>
    public string LatinumBaseUrl { get; private set; } = null!;

    public string PostgresConnectionString => _postgres.GetConnectionString();

    /// <summary>Host-reachable Redis connection string (mapped port), for driving/inspecting jobs.</summary>
    public string RedisHostConnectionString { get; private set; } = null!;

    public IDataContext NewDataContext() => new PostgreSqlDataContext(PostgresConnectionString);

    /// <summary>Creates a Redis job context bound to <paramref name="channel"/> over the host-mapped port.</summary>
    public RedisJobContext NewJobContext(string channel)
    {
        var ctx = new RedisJobContext();
        ctx.Initialize(RedisHostConnectionString, channel);
        return ctx;
    }

    public HttpClient NewGatewayClient() => new() { BaseAddress = new Uri(GatewayBaseUrl) };
    public HttpClient NewTrustGatewayClient() => new() { BaseAddress = new Uri(TrustGatewayBaseUrl) };
    public HttpClient NewLatinumClient() => new() { BaseAddress = new Uri(LatinumBaseUrl) };

    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder().WithName($"warp-e2e-{Guid.NewGuid():N}").Build();
        await _network.CreateAsync();

        // --- Infrastructure containers (independent, start first) ---
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase(PgDb).WithUsername(PgUser).WithPassword(PgPass)
            .WithNetwork(_network).WithNetworkAliases("postgres")
            .Build();

        _redis = new ContainerBuilder()
            .WithImage(RedisImage)
            .WithNetwork(_network).WithNetworkAliases("redis")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(6379))
            .Build();

        _localstripe = new ContainerBuilder()
            .WithImage(LocalStripeImage)
            .WithNetwork(_network).WithNetworkAliases("localstripe")
            .WithPortBinding(8420, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(8420))
            .Build();

        _echo = new ContainerBuilder()
            .WithImage(EchoImage)
            .WithNetwork(_network).WithNetworkAliases("echo")
            .WithEnvironment("HTTP_PORT", "8080")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(8080))
            .Build();

        await Task.WhenAll(
            _postgres.StartAsync(),
            _redis.StartAsync(),
            _localstripe.StartAsync(),
            _echo.StartAsync());

        // Redis is host-mapped so the tests can enqueue/inspect jobs directly (used to drive the
        // subscription webhook, whose gateway session-create path is incompatible with this
        // localstripe build, and to read completed async job results at the data layer).
        RedisHostConnectionString = $"{_redis.Hostname}:{_redis.GetMappedPublicPort(6379)},defaultDatabase=0";

        // Pre-create the schema from the test side so the warp containers don't race on DDL.
        _ = NewDataContext().Quotas.FirstOrDefault();

        // --- Build the warp runtime image from freshly published binaries + e2e config ---
        _image = await BuildWarpImageAsync();

        // --- Warp processes ---
        _gateway = new ContainerBuilder()
            .WithImage(_image)
            .WithNetwork(_network).WithNetworkAliases("warp")
            .WithCommand("dotnet", "warp.dll")
            .WithEnvironment(WarpEnv(("ASPNETCORE_URLS", "http://0.0.0.0:5000")))
            .WithPortBinding(5000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Application started"))
            .Build();

        // Second gateway WITH the shared secret so the trust-marker inject/strip behaviour can be
        // asserted (scenario 5) without stripping X-JWT-Email on the identity-bearing routes.
        _trustGateway = new ContainerBuilder()
            .WithImage(_image)
            .WithNetwork(_network).WithNetworkAliases("warp-trust")
            .WithCommand("dotnet", "warp.dll")
            .WithEnvironment(WarpEnv(
                ("ASPNETCORE_URLS", "http://0.0.0.0:5000"),
                ("GATEWAY_SHARED_SECRET", GatewaySharedSecret)))
            .WithPortBinding(5000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Application started"))
            .Build();

        _plasma = new ContainerBuilder()
            .WithImage(_image)
            .WithNetwork(_network).WithNetworkAliases("warp-plasma")
            .WithCommand("dotnet", "warp.plasma.dll")
            .WithEnvironment(WarpEnv())
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("job processor|Started|Application started|Processor"))
            .Build();

        _latinum = new ContainerBuilder()
            .WithImage(_image)
            .WithNetwork(_network).WithNetworkAliases("warp-latinum")
            .WithCommand("dotnet", "warp.latinum.dll")
            .WithEnvironment(WarpEnv())
            .WithPortBinding(5004, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Application started"))
            .Build();

        await Task.WhenAll(
            _gateway.StartAsync(), _trustGateway.StartAsync(),
            _plasma.StartAsync(), _latinum.StartAsync());

        GatewayBaseUrl = $"http://{_gateway.Hostname}:{_gateway.GetMappedPublicPort(5000)}";
        TrustGatewayBaseUrl = $"http://{_trustGateway.Hostname}:{_trustGateway.GetMappedPublicPort(5000)}";
        LatinumBaseUrl = $"http://{_latinum.Hostname}:{_latinum.GetMappedPublicPort(5004)}";
    }

    private Dictionary<string, string> WarpEnv(params (string k, string v)[] extra)
    {
        var env = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["WARP_CONFIG_BASE_DIR"] = "/warp/config",
            ["WARP_PG_CONN"] = PgConnInternal,
            ["REDIS_CONNECTION_STRING"] = RedisConn,
            ["STRIPE_API_BASE"] = StripeApiBaseInternal,
            ["STRIPE_SECRET_KEY"] = "sk_test_e2e",
            ["STRIPE_PUBLISHABLE_KEY"] = "pk_test_e2e",
            ["STRIPE_WEBHOOK_SECRET"] = WebhookSecret,
            ["ECHO_ADDR"] = EchoAddrInternal,
        };
        foreach (var (k, v) in extra) env[k] = v;
        return env;
    }

    private async Task<IFutureDockerImage> BuildWarpImageAsync()
    {
        var repoRoot = FindRepoRoot();
        var e2eDir = Path.Combine(repoRoot, "tests", "Warp.E2E.Tests");
        var dockerfileSrc = Path.Combine(e2eDir, "compose", "Dockerfile");
        var configSrc = Path.Combine(e2eDir, "config");

        _buildContext = Path.Combine(Path.GetTempPath(), $"warp-e2e-ctx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_buildContext);
        File.Copy(dockerfileSrc, Path.Combine(_buildContext, "Dockerfile"));
        CopyDirectory(configSrc, Path.Combine(_buildContext, "config"));

        var publishDir = Path.Combine(_buildContext, "publish");
        Directory.CreateDirectory(publishDir);

        var prePublished = Environment.GetEnvironmentVariable("WARP_E2E_PUBLISH_DIR");
        if (!string.IsNullOrEmpty(prePublished) && Directory.Exists(prePublished))
        {
            CopyDirectory(prePublished, publishDir);
        }
        else
        {
            foreach (var proj in new[] { "warp", "warp.plasma", "warp.latinum" })
                await PublishAsync(Path.Combine(repoRoot, proj, $"{proj}.csproj"), publishDir);
        }

        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(_buildContext)
            .WithDockerfile("Dockerfile")
            .WithName($"warp-e2e:{Guid.NewGuid():N}")
            .WithDeleteIfExists(true)
            .Build();

        await image.CreateAsync();
        return image;
    }

    private static async Task PublishAsync(string csproj, string outputDir)
    {
        var psi = new ProcessStartInfo("dotnet",
            $"publish \"{csproj}\" -c Release -o \"{outputDir}\" --nologo -v quiet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        // Read both streams concurrently to avoid a pipe-buffer deadlock when the
        // child fills stderr while we block reading stdout (NU1902 restore warnings, etc.).
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"dotnet publish failed for {csproj}:\n{stdout}\n{stderr}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "warp.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (warp.sln).");
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(source))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    /// <summary>Dumps recent logs from the warp containers (for diagnosing a failed run).</summary>
    public async Task<string> DumpWarpLogsAsync()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (name, c) in new[] { ("gateway", _gateway), ("trust", _trustGateway), ("plasma", _plasma), ("latinum", _latinum) })
        {
            if (c is null) continue;
            var (stdout, stderr) = await c.GetLogsAsync();
            sb.AppendLine($"===== {name} stdout =====").AppendLine(stdout);
            sb.AppendLine($"===== {name} stderr =====").AppendLine(stderr);
        }
        return sb.ToString();
    }

    public async Task DisposeAsync()
    {
        foreach (var c in new[] { _gateway, _trustGateway, _plasma, _latinum, _echo, _localstripe, _redis })
        {
            if (c is not null)
                try { await c.DisposeAsync(); } catch { /* best effort */ }
        }
        if (_postgres is not null)
            try { await _postgres.DisposeAsync(); } catch { }
        if (_image is not null)
            try { await _image.DisposeAsync(); } catch { }
        if (_network is not null)
            try { await _network.DeleteAsync(); } catch { }
        if (_buildContext is not null && Directory.Exists(_buildContext))
            try { Directory.Delete(_buildContext, recursive: true); } catch { }
    }
}

/// <summary>Shares one stack across every e2e test class (the stack is expensive to build).</summary>
[CollectionDefinition("e2e")]
public sealed class E2ECollection : ICollectionFixture<E2EStack> { }

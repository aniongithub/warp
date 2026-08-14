using Microsoft.Extensions.Configuration;
using Warp.Core.Helper;

namespace Warp.Core.Tests;

/// <summary>
/// Coverage for <see cref="ConfigurationExtensions.AddWarpConfiguration"/>: the <c>$include</c>
/// directive in all three shapes (direct merge, keyed <c>$include:key</c>, and the
/// <c>Parent: { $include: file }</c> form), <c>${VAR}</c> / <c>${VAR:default}</c> env interpolation,
/// and the iterative resolution where an env var feeds an include path.
///
/// Each test writes real YAML into a fresh temp directory and builds a real configuration, so the
/// full deserialize → process → merge → reload path is exercised end to end.
/// </summary>
public class ConfigurationExtensionsTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _envVars = new();

    public ConfigurationExtensionsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "warp-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void SetEnv(string name, string? value)
    {
        _envVars.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private IConfigurationRoot Build(string baseName = "gateway")
        => new ConfigurationBuilder()
            .AddWarpConfiguration(baseName, _dir, useDevelopmentConfig: false, clearExistingSources: true)
            .Build();

    [Fact]
    public void DirectInclude_MergesTopLevelKeys()
    {
        Write("gateway.yml", "App: WarpTest\n$include: base.yml\n");
        Write("base.yml", "Shared: fromBase\nRegion: eu\n");

        var config = Build();

        config["App"].Should().Be("WarpTest");
        config["Shared"].Should().Be("fromBase");
        config["Region"].Should().Be("eu");
        config["$include"].Should().BeNull("the directive must be removed after processing");
    }

    [Fact]
    public void KeyedInclude_MergesUnderTargetKey()
    {
        Write("gateway.yml", "App: WarpTest\n\"$include:Plans\": plans.yml\n");
        Write("plans.yml", "Free: 100\nPro: 1000\n");

        var config = Build();

        config["App"].Should().Be("WarpTest");
        config["Plans:Free"].Should().Be("100");
        config["Plans:Pro"].Should().Be("1000");
    }

    [Fact]
    public void ParentBlockInclude_MergesUnderParentKey()
    {
        Write("gateway.yml", "Database:\n  $include: db.yml\n");
        Write("db.yml", "Host: localhost\nPort: 5432\n");

        var config = Build();

        config["Database:Host"].Should().Be("localhost");
        config["Database:Port"].Should().Be("5432");
    }

    [Fact]
    public void EnvInterpolation_ResolvesValueAndDefault()
    {
        SetEnv("WARP_TEST_TOKEN", "s3cret");
        SetEnv("WARP_TEST_REGION", null); // unset => default applies

        Write("gateway.yml", "Token: ${WARP_TEST_TOKEN}\nRegion: ${WARP_TEST_REGION:us-east-1}\n");

        var config = Build();

        config["Token"].Should().Be("s3cret");
        config["Region"].Should().Be("us-east-1");
    }

    [Fact]
    public void EnvInterpolation_InsideIncludedFile_IsResolved()
    {
        SetEnv("WARP_TEST_MERGED", "resolved-in-include");

        Write("gateway.yml", "$include: secrets.yml\n");
        Write("secrets.yml", "MergedToken: ${WARP_TEST_MERGED}\n");

        var config = Build();

        config["MergedToken"].Should().Be("resolved-in-include");
    }

    [Fact]
    public void Iterative_EnvVar_FeedsIncludePath()
    {
        // The env var is resolved first, producing the include path that is then followed on the
        // same pass — proving the interleaved env-then-include resolution loop.
        SetEnv("WARP_TEST_SUBDIR", "inner");

        Write("gateway.yml", "Nested:\n  $include: ${WARP_TEST_SUBDIR}/nested.yml\n");
        Write("inner/nested.yml", "Deep: works\n");

        var config = Build();

        config["Nested:Deep"].Should().Be("works");
    }

    public void Dispose()
    {
        foreach (var name in _envVars)
            Environment.SetEnvironmentVariable(name, null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

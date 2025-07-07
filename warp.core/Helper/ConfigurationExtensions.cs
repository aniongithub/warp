namespace Warp.Core.Helper;

using Microsoft.Extensions.Configuration;

public static class ConfigurationExtensions
{
    public static IConfigurationBuilder AddWarpConfiguration(this IConfigurationBuilder builder, string baseName, string baseDirectory = "./config", bool useDevelopmentConfig = true, bool clearExistingSources = false)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var configFile = $"{baseName}.json";
        var devConfigFile = $"{baseName}.Development.json";

        // Make baseDirectory absolute if it is relative
        if (!Path.IsPathRooted(baseDirectory))
            baseDirectory = Path.Combine(Directory.GetCurrentDirectory(), baseDirectory);

        // Clear existing sources if requested (useful when you want only custom config)
        if (clearExistingSources)
            builder.Sources.Clear();

        builder
            .SetBasePath(baseDirectory)
            .AddJsonFile(configFile, optional: false, reloadOnChange: true);

        if (useDevelopmentConfig && environment == "Development")
            builder.AddJsonFile(devConfigFile, optional: true, reloadOnChange: true);

        return builder;
    }
}
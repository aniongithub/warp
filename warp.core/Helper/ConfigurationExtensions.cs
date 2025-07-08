namespace Warp.Core.Helper;

using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class ConfigurationExtensions
{
    public static IConfigurationBuilder AddWarpConfiguration(this IConfigurationBuilder builder, string baseName, string baseDirectory = "./config", bool useDevelopmentConfig = true, bool clearExistingSources = false)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var configFile = $"{baseName}.jsonc";
        var devConfigFile = $"{baseName}.Development.jsonc";

        // Make baseDirectory absolute if it is relative
        if (!Path.IsPathRooted(baseDirectory))
            baseDirectory = Path.Combine(Directory.GetCurrentDirectory(), baseDirectory);

        // Clear existing sources if requested (useful when you want only custom config)
        if (clearExistingSources)
            builder.Sources.Clear();

        // Process includes and merge configurations
        var mergedConfig = ProcessConfigurationWithIncludes(Path.Combine(baseDirectory, configFile), baseDirectory);
        
        // Create a temporary file with merged configuration
        var tempConfigFile = Path.GetTempFileName();
        File.WriteAllText(tempConfigFile, mergedConfig);

        builder
            .SetBasePath(baseDirectory)
            .AddJsonFile(tempConfigFile, optional: false, reloadOnChange: false);

        if (useDevelopmentConfig && environment == "Development")
        {
            var devConfigPath = Path.Combine(baseDirectory, devConfigFile);
            if (File.Exists(devConfigPath))
            {
                var mergedDevConfig = ProcessConfigurationWithIncludes(devConfigPath, baseDirectory);
                var tempDevConfigFile = Path.GetTempFileName();
                File.WriteAllText(tempDevConfigFile, mergedDevConfig);
                builder.AddJsonFile(tempDevConfigFile, optional: true, reloadOnChange: false);
            }
        }

        return builder;
    }

    private static string ProcessConfigurationWithIncludes(string configFilePath, string baseDirectory)
    {
        if (!File.Exists(configFilePath))
            throw new FileNotFoundException($"Configuration file not found: {configFilePath}");

        var configContent = File.ReadAllText(configFilePath);
        
        // Parse as JsonNode to handle JSONC (comments)
        var jsonNode = JsonNode.Parse(configContent, new JsonNodeOptions
        {
            PropertyNameCaseInsensitive = false
        }, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (jsonNode is JsonObject rootObject)
        {
            ProcessIncludes(rootObject, baseDirectory);
        }

        return jsonNode?.ToJsonString(new JsonSerializerOptions 
        { 
            WriteIndented = true 
        }) ?? "{}";
    }

    private static void ProcessIncludes(JsonObject jsonObject, string baseDirectory)
    {
        var includesToProcess = new List<(string key, string includePath)>();

        // Find all include directives
        foreach (var kvp in jsonObject.ToArray())
        {
            if (kvp.Key.StartsWith("$include:", StringComparison.OrdinalIgnoreCase))
            {
                var includePath = kvp.Value?.ToString();
                if (!string.IsNullOrEmpty(includePath))
                {
                    includesToProcess.Add((kvp.Key, includePath));
                }
            }
            else if (kvp.Value is JsonObject nestedObject)
            {
                ProcessIncludes(nestedObject, baseDirectory);
            }
        }

        // Process includes
        foreach (var (key, includePath) in includesToProcess)
        {
            var fullIncludePath = Path.IsPathRooted(includePath) 
                ? includePath 
                : Path.Combine(baseDirectory, includePath);

            if (File.Exists(fullIncludePath))
            {
                var includeContent = File.ReadAllText(fullIncludePath);
                var includeNode = JsonNode.Parse(includeContent, new JsonNodeOptions
                {
                    PropertyNameCaseInsensitive = false
                }, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                if (includeNode is JsonObject includeObject)
                {
                    // Process nested includes in the included file
                    ProcessIncludes(includeObject, baseDirectory);

                    // Merge the included configuration
                    foreach (var includeKvp in includeObject)
                    {
                        jsonObject[includeKvp.Key] = includeKvp.Value?.DeepClone();
                    }
                }
            }

            // Remove the include directive
            jsonObject.Remove(key);
        }
    }
}
namespace Warp.Core.Helper;

using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
            ProcessConfigurationRecursively(rootObject, baseDirectory);
        }

        return jsonNode?.ToJsonString(new JsonSerializerOptions 
        { 
            WriteIndented = true 
        }) ?? "{}";
    }

    private static void ProcessConfigurationRecursively(JsonObject jsonObject, string baseDirectory, int maxIterations = 10)
    {
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            bool hasChanges = false;

            // First pass: Process environment variables (so they can be used in include paths)
            hasChanges |= ProcessEnvironmentVariables(jsonObject);

            // Second pass: Process includes (which may introduce new env vars to process)
            hasChanges |= ProcessIncludes(jsonObject, baseDirectory);

            // If no changes were made in this iteration, we're done
            if (!hasChanges)
                break;

            // If we've reached max iterations, warn but continue
            if (iteration == maxIterations - 1)
            {
                Console.WriteLine($"Warning: Maximum iterations ({maxIterations}) reached during configuration processing. Some environment variables or includes may not be fully resolved.");
            }
        }
    }

    private static bool ProcessIncludes(JsonObject jsonObject, string baseDirectory)
    {
        var includesToProcess = new List<(string key, string includePath)>();
        bool hasChanges = false;

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
                hasChanges |= ProcessIncludes(nestedObject, baseDirectory);
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
                    // Process nested includes in the included file recursively
                    ProcessIncludes(includeObject, baseDirectory);

                    // Merge the included configuration
                    foreach (var includeKvp in includeObject)
                    {
                        jsonObject[includeKvp.Key] = includeKvp.Value?.DeepClone();
                    }
                    hasChanges = true;
                }
            }

            // Remove the include directive
            jsonObject.Remove(key);
            hasChanges = true;
        }

        return hasChanges;
    }

    private static bool ProcessEnvironmentVariables(JsonObject jsonObject)
    {
        // Regex to match ${VAR_NAME} or ${VAR_NAME:default_value} patterns
        var envVarRegex = new Regex(@"\$\{([A-Za-z_][A-Za-z0-9_]*?)(?::([^}]*))?\}", RegexOptions.Compiled);
        bool hasChanges = false;

        foreach (var kvp in jsonObject.ToArray())
        {
            if (kvp.Value is JsonValue jsonValue)
            {
                var stringValue = jsonValue.ToString();
                if (envVarRegex.IsMatch(stringValue))
                {
                    var interpolatedValue = envVarRegex.Replace(stringValue, match =>
                    {
                        var varName = match.Groups[1].Value;
                        var defaultValue = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
                        
                        var envValue = Environment.GetEnvironmentVariable(varName);
                        return envValue ?? defaultValue;
                    });
                    
                    jsonObject[kvp.Key] = JsonValue.Create(interpolatedValue);
                    hasChanges = true;
                }
            }
            else if (kvp.Value is JsonObject nestedObject)
            {
                hasChanges |= ProcessEnvironmentVariables(nestedObject);
            }
            else if (kvp.Value is JsonArray jsonArray)
            {
                hasChanges |= ProcessEnvironmentVariablesInArray(jsonArray);
            }
        }

        return hasChanges;
    }

    private static bool ProcessEnvironmentVariablesInArray(JsonArray jsonArray)
    {
        var envVarRegex = new Regex(@"\$\{([A-Za-z_][A-Za-z0-9_]*?)(?::([^}]*))?\}", RegexOptions.Compiled);
        bool hasChanges = false;

        for (int i = 0; i < jsonArray.Count; i++)
        {
            var item = jsonArray[i];
            
            if (item is JsonValue jsonValue)
            {
                var stringValue = jsonValue.ToString();
                if (envVarRegex.IsMatch(stringValue))
                {
                    var interpolatedValue = envVarRegex.Replace(stringValue, match =>
                    {
                        var varName = match.Groups[1].Value;
                        var defaultValue = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
                        
                        var envValue = Environment.GetEnvironmentVariable(varName);
                        return envValue ?? defaultValue;
                    });
                    
                    jsonArray[i] = JsonValue.Create(interpolatedValue);
                    hasChanges = true;
                }
            }
            else if (item is JsonObject nestedObject)
            {
                hasChanges |= ProcessEnvironmentVariables(nestedObject);
            }
            else if (item is JsonArray nestedArray)
            {
                hasChanges |= ProcessEnvironmentVariablesInArray(nestedArray);
            }
        }

        return hasChanges;
    }
}
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
            ProcessIncludes(rootObject, baseDirectory);
            ProcessEnvironmentVariables(rootObject);
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

    private static void ProcessEnvironmentVariables(JsonObject jsonObject)
    {
        // Regex to match ${VAR_NAME} or ${VAR_NAME:default_value} patterns
        var envVarRegex = new Regex(@"\$\{([A-Za-z_][A-ZaZ0-9_]*?)(?::([^}]*))?\}", RegexOptions.Compiled);

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
                }
            }
            else if (kvp.Value is JsonObject nestedObject)
            {
                ProcessEnvironmentVariables(nestedObject);
            }
            else if (kvp.Value is JsonArray jsonArray)
            {
                ProcessEnvironmentVariablesInArray(jsonArray);
            }
        }
    }

    private static void ProcessEnvironmentVariablesInArray(JsonArray jsonArray)
    {
        var envVarRegex = new Regex(@"\$\{([A-Za-z_][A-ZaZ0-9_]*?)(?::([^}]*))?\}", RegexOptions.Compiled);

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
                }
            }
            else if (item is JsonObject nestedObject)
            {
                ProcessEnvironmentVariables(nestedObject);
            }
            else if (item is JsonArray nestedArray)
            {
                ProcessEnvironmentVariablesInArray(nestedArray);
            }
        }
    }
}
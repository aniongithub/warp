namespace Warp.Core.Helper;

using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Collections;
using System.IO;

public static class ConfigurationExtensions
{
    public static IConfigurationBuilder AddWarpConfiguration(this IConfigurationBuilder builder, string baseName, string baseDirectory = "./config", bool useDevelopmentConfig = true, bool clearExistingSources = false)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var configFile = $"{baseName}.yml";
        var devConfigFile = $"{baseName}.Development.yml";

        // Make baseDirectory absolute if it is relative
        if (!Path.IsPathRooted(baseDirectory))
            baseDirectory = Path.Combine(Directory.GetCurrentDirectory(), baseDirectory);

        // Clear existing sources if requested (useful when you want only custom config)
        if (clearExistingSources)
            builder.Sources.Clear();

        // Process includes and merge configurations
        var mergedConfig = ProcessConfigurationWithIncludes(Path.Combine(baseDirectory, configFile), baseDirectory);
        
        // Create a temporary YAML file with merged configuration
        var tempConfigFile = Path.ChangeExtension(Path.GetTempFileName(), ".yml");
        File.WriteAllText(tempConfigFile, mergedConfig);

        builder
            .SetBasePath(baseDirectory)
            .AddYamlFile(tempConfigFile, optional: false, reloadOnChange: false);

        if (useDevelopmentConfig && environment == "Development")
        {
            var devConfigPath = Path.Combine(baseDirectory, devConfigFile);
            if (File.Exists(devConfigPath))
            {
                var mergedDevConfig = ProcessConfigurationWithIncludes(devConfigPath, baseDirectory);
                var tempDevConfigFile = Path.ChangeExtension(Path.GetTempFileName(), ".yml");
                File.WriteAllText(tempDevConfigFile, mergedDevConfig);
                builder.AddYamlFile(tempDevConfigFile, optional: true, reloadOnChange: false);
            }
        }

        return builder;
    }

    private static string ProcessConfigurationWithIncludes(string configFilePath, string baseDirectory)
    {
        if (!File.Exists(configFilePath))
            throw new FileNotFoundException($"Configuration file not found: {configFilePath}");

        var configContent = File.ReadAllText(configFilePath);
        
        var deserializer = new DeserializerBuilder()
            .Build();
        
        // Deserialize to object and normalize to string-keyed dictionaries so recursion works everywhere
        var root = deserializer.Deserialize<object>(configContent);
        var yamlObject = NormalizeYaml(root) as Dictionary<string, object>;
        
        if (yamlObject != null)
        {
            ProcessConfigurationRecursively(yamlObject, baseDirectory);
        }

        var serializer = new SerializerBuilder()
            .Build();
        
        return serializer.Serialize(yamlObject ?? new Dictionary<string, object>());
    }

    private static void ProcessConfigurationRecursively(Dictionary<string, object> yamlObject, string baseDirectory, int maxIterations = 10)
    {
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            bool hasChanges = false;

            // First pass: Process environment variables (so they can be used in include paths)
            hasChanges |= ProcessEnvironmentVariables(yamlObject);

            // Second pass: Process includes (which may introduce new env vars to process)
            hasChanges |= ProcessIncludes(yamlObject, baseDirectory);

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

    private static bool ProcessIncludes(Dictionary<string, object> yamlObject, string baseDirectory)
    {
        var includesToProcess = new List<(string key, string includePath)>();
        bool hasChanges = false;
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        // Find all include directives
        foreach (var kvp in yamlObject.ToArray())
        {
            if (kvp.Key.StartsWith("$include:", StringComparison.OrdinalIgnoreCase))
            {
                var includePath = kvp.Value?.ToString();
                if (!string.IsNullOrEmpty(includePath))
                {
                    includesToProcess.Add((kvp.Key, includePath));
                }
            }
            else if (kvp.Value is Dictionary<string, object> nestedObject)
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
                var includeRoot = deserializer.Deserialize<object>(includeContent);
                var includeObject = NormalizeYaml(includeRoot) as Dictionary<string, object>;

                if (includeObject != null)
                {
                    // Process nested includes and env vars in the included file recursively
                    ProcessConfigurationRecursively(includeObject, baseDirectory);

                    // Merge the included configuration
                    foreach (var includeKvp in includeObject)
                    {
                        yamlObject[includeKvp.Key] = includeKvp.Value;
                    }
                    hasChanges = true;
                }
            }

            // Remove the include directive
            yamlObject.Remove(key);
            hasChanges = true;
        }

        return hasChanges;
    }

    private static bool ProcessEnvironmentVariables(Dictionary<string, object> yamlObject)
    {
        // Regex to match ${VAR_NAME} or ${VAR_NAME:default_value} patterns
        var envVarRegex = new Regex(@"\$\{([A-Za-z_][A-Za-z0-9_]*?)(?::([^}]*))?\}", RegexOptions.Compiled);
        bool hasChanges = false;

        foreach (var kvp in yamlObject.ToArray())
        {
            if (kvp.Value is string stringValue)
            {
                if (envVarRegex.IsMatch(stringValue))
                {
                    var interpolatedValue = envVarRegex.Replace(stringValue, match =>
                    {
                        var varName = match.Groups[1].Value;
                        var defaultValue = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
                        
                        var envValue = Environment.GetEnvironmentVariable(varName);
                        return envValue ?? defaultValue;
                    });
                    
                    yamlObject[kvp.Key] = interpolatedValue;
                    hasChanges = true;
                }
            }
            else if (kvp.Value is Dictionary<string, object> nestedObject)
            {
                hasChanges |= ProcessEnvironmentVariables(nestedObject);
            }
            else if (kvp.Value is List<object> yamlArray)
            {
                hasChanges |= ProcessEnvironmentVariablesInArray(yamlArray);
            }
        }

        return hasChanges;
    }

    private static bool ProcessEnvironmentVariablesInArray(List<object> yamlArray)
    {
        var envVarRegex = new Regex(@"\$\{([A-Za-z_][A-Za-z0-9_]*?)(?::([^}]*))?\}", RegexOptions.Compiled);
        bool hasChanges = false;

        for (int i = 0; i < yamlArray.Count; i++)
        {
            var item = yamlArray[i];
            
            if (item is string stringValue)
            {
                if (envVarRegex.IsMatch(stringValue))
                {
                    var interpolatedValue = envVarRegex.Replace(stringValue, match =>
                    {
                        var varName = match.Groups[1].Value;
                        var defaultValue = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
                        
                        var envValue = Environment.GetEnvironmentVariable(varName);
                        return envValue ?? defaultValue;
                    });
                    
                    yamlArray[i] = interpolatedValue;
                    hasChanges = true;
                }
            }
            else if (item is Dictionary<string, object> nestedObject)
            {
                hasChanges |= ProcessEnvironmentVariables(nestedObject);
            }
            else if (item is List<object> nestedArray)
            {
                hasChanges |= ProcessEnvironmentVariablesInArray(nestedArray);
            }
        }

        return hasChanges;
    }

    // Normalize YamlDotNet default types (Dictionary<object, object>/IList) to Dictionary<string, object>/List<object>
    private static object NormalizeYaml(object? node)
    {
        if (node is IDictionary dict)
        {
            var result = new Dictionary<string, object>();
            foreach (DictionaryEntry entry in dict)
            {
                var key = entry.Key?.ToString() ?? string.Empty;
                result[key] = NormalizeYaml(entry.Value);
            }
            return result;
        }
        if (node is IEnumerable enumerable && node is not string)
        {
            var list = new List<object>();
            foreach (var item in enumerable)
            {
                list.Add(NormalizeYaml(item));
            }
            return list;
        }
        return node ?? string.Empty;
    }
}
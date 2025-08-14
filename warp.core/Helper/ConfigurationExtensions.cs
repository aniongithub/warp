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
            if (kvp.Key.StartsWith("$include:", StringComparison.OrdinalIgnoreCase) || 
                kvp.Key == "$include")
            {
                var includePath = kvp.Value?.ToString();
                if (!string.IsNullOrEmpty(includePath))
                {
                    // Check if this is a wildcard include
                    if (includePath.Contains('*'))
                    {
                        var wildcardIncludes = ProcessWildcardInclude(kvp.Key, includePath, baseDirectory);
                        foreach (var (wildcardKey, wildcardPath) in wildcardIncludes)
                        {
                            includesToProcess.Add((wildcardKey, wildcardPath));
                        }
                    }
                    else
                    {
                        includesToProcess.Add((kvp.Key, includePath));
                    }
                }
            }
            else if (kvp.Value is JsonObject nestedObject)
            {
                hasChanges |= ProcessIncludes(nestedObject, baseDirectory);
            }
            else if (kvp.Value is JsonArray jsonArray)
            {
                // Process includes within arrays
                hasChanges |= ProcessIncludesInArray(jsonArray, baseDirectory);
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
                    // Process nested includes and environment variables in the included file recursively
                    ProcessConfigurationRecursively(includeObject, baseDirectory);

                    // Check if this is a direct object include or named include
                    if (key == "$include")
                    {
                        // Direct merge - includes all properties from the file
                        DeepMergeJsonObjects(jsonObject, includeObject);
                    }
                    else if (key.StartsWith("$include:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Named include - extract target property name after "$include:"
                        var targetKey = key.Substring("$include:".Length);
                        if (!string.IsNullOrEmpty(targetKey))
                        {
                            jsonObject[targetKey] = includeObject.DeepClone();
                        }
                        else
                        {
                            // Fallback to direct merge if no target key specified
                            DeepMergeJsonObjects(jsonObject, includeObject);
                        }
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

    private static List<(string key, string path)> ProcessWildcardInclude(string originalKey, string wildcardPath, string baseDirectory)
    {
        var results = new List<(string key, string path)>();
        
        // Convert wildcard pattern to full path
        var fullWildcardPath = Path.IsPathRooted(wildcardPath) 
            ? wildcardPath 
            : Path.Combine(baseDirectory, wildcardPath);
            
        var directory = Path.GetDirectoryName(fullWildcardPath);
        var pattern = Path.GetFileName(fullWildcardPath);
        
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(pattern) || !Directory.Exists(directory))
            return results;
            
        // Create regex pattern from wildcard pattern to extract the match portion
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", "(.*)") + "$";
        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase);
            
        var matchingFiles = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        
        foreach (var filePath in matchingFiles)
        {
            var fileName = Path.GetFileName(filePath);
            var match = regex.Match(fileName);
            var wildcardMatch = match.Success && match.Groups.Count > 1 ? match.Groups[1].Value : fileName;
            
            if (originalKey == "$include")
            {
                // For direct includes, just add the file path
                results.Add((originalKey, filePath));
            }
            else if (originalKey.StartsWith("$include:", StringComparison.OrdinalIgnoreCase))
            {
                // For named includes, check if it has () placeholder
                var targetKeyPart = originalKey.Substring("$include:".Length);
                
                if (targetKeyPart.Contains("()"))
                {
                    // Replace () with the wildcard match portion
                    var actualKey = "$include:" + targetKeyPart.Replace("()", wildcardMatch);
                    results.Add((actualKey, filePath));
                }
                else
                {
                    // No placeholder, use original key (this will merge all files into same property)
                    results.Add((originalKey, filePath));
                }
            }
        }
        
        return results;
    }

    private static bool ProcessIncludesInArray(JsonArray jsonArray, string baseDirectory)
    {
        bool hasChanges = false;
        
        for (int i = jsonArray.Count - 1; i >= 0; i--) // Process backwards to handle replacements
        {
            var item = jsonArray[i];
            
            if (item is JsonObject itemObject)
            {
                // Check if this object has an include directive
                if (itemObject.ContainsKey("$include"))
                {
                    var includePath = itemObject["$include"]?.ToString();
                    if (!string.IsNullOrEmpty(includePath))
                    {
                        if (includePath.Contains('*'))
                        {
                            // Handle wildcard includes in arrays
                            var wildcardIncludes = ProcessWildcardInclude("$include", includePath, baseDirectory);
                            
                            // Remove the original include object
                            jsonArray.RemoveAt(i);
                            
                            // Add all wildcard matches at the same position
                            int insertIndex = i;
                            foreach (var (_, wildcardPath) in wildcardIncludes)
                            {
                                if (File.Exists(wildcardPath))
                                {
                                    var includeContent = File.ReadAllText(wildcardPath);
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
                                        // Process nested includes and environment variables recursively
                                        ProcessConfigurationRecursively(includeObject, baseDirectory);
                                        
                                        // Insert the included content
                                        jsonArray.Insert(insertIndex++, includeObject.DeepClone());
                                        hasChanges = true;
                                    }
                                }
                            }
                        }
                        else
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
                                    // Process nested includes and environment variables recursively
                                    ProcessConfigurationRecursively(includeObject, baseDirectory);
                                    
                                    // Replace the include object with the included content
                                    jsonArray[i] = includeObject.DeepClone();
                                    hasChanges = true;
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Process includes within this object
                    hasChanges |= ProcessIncludes(itemObject, baseDirectory);
                }
            }
            else if (item is JsonArray nestedArray)
            {
                hasChanges |= ProcessIncludesInArray(nestedArray, baseDirectory);
            }
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

    private static void DeepMergeJsonObjects(JsonObject target, JsonObject source)
    {
        foreach (var kvp in source)
        {
            if (target.ContainsKey(kvp.Key))
            {
                // Key exists in target, check if both are objects for deep merge
                if (target[kvp.Key] is JsonObject targetObject && kvp.Value is JsonObject sourceObject)
                {
                    DeepMergeJsonObjects(targetObject, sourceObject);
                }
                else if (target[kvp.Key] is JsonArray targetArray && kvp.Value is JsonArray sourceArray)
                {
                    // For arrays, append source items to target (you could change this behavior)
                    foreach (var item in sourceArray)
                    {
                        targetArray.Add(item?.DeepClone());
                    }
                }
                else
                {
                    // Different types or primitive values - overwrite with source
                    target[kvp.Key] = kvp.Value?.DeepClone();
                }
            }
            else
            {
                // Key doesn't exist in target, add it
                target[kvp.Key] = kvp.Value?.DeepClone();
            }
        }
    }
}
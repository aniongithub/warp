using Warp.Core.Job;

namespace Warp.Dilithium.Transforms;

/// <summary>
/// Extension methods for Transform-related operations
/// </summary>
public static class TransformExtensions
{
    /// <summary>
    /// Creates a transform instance from a transform configuration
    /// </summary>
    /// <param name="transformConfig">The transform configuration</param>
    /// <returns>An instance of the specified transform</returns>
    /// <exception cref="InvalidOperationException">Thrown when the transform type cannot be found or instantiated</exception>
    public static ITransform CreateTransform(this TransformConfig transformConfig)
    {
        var transformType = Type.GetType(transformConfig.Type);
        if (transformType == null)
            throw new InvalidOperationException($"Transform type not found: {transformConfig.Type}");

        var optionsType = transformType.BaseType?.GetGenericArguments().FirstOrDefault();
        if (optionsType == null)
            throw new InvalidOperationException($"Transform type {transformConfig.Type} does not have a parameterless constructor or options type");
        
        var options = Activator.CreateInstance(optionsType);
        if (options == null)
            throw new InvalidOperationException($"Could not create options instance for transform type {transformConfig.Type}");

        // Copy all properties from the options dictionary to the options object
        foreach (var kvp in transformConfig.Options)
        {
            var prop = optionsType.GetProperty(kvp.Key);
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    // Handle null values
                    if (kvp.Value == null)
                    {
                        if (prop.PropertyType.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) == null)
                        {
                            // Cannot assign null to non-nullable value type
                            continue;
                        }
                        prop.SetValue(options, null);
                    }
                    else
                    {
                        // Try direct assignment first, then conversion
                        if (prop.PropertyType.IsAssignableFrom(kvp.Value.GetType()))
                        {
                            prop.SetValue(options, kvp.Value);
                        }
                        else
                        {
                            // Convert the value to the target property type
                            var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                            var convertedValue = Convert.ChangeType(kvp.Value, targetType);
                            prop.SetValue(options, convertedValue);
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to set property {kvp.Key} on transform options of type {optionsType.Name}: {ex.Message}", ex);
                }
            }
        }

        return (ITransform)Activator.CreateInstance(transformType, options)!;
    }
}

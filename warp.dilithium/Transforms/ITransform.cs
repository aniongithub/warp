namespace Warp.Dilithium.Transforms;

/// <summary>
/// Non-generic base interface for transforms to avoid casting issues
/// </summary>
public interface ITransform
{
    /// <summary>
    /// Transform input data (e.g., file content to S3 URI).
    /// Called during async job creation after data has been extracted from HTTP context.
    /// </summary>
    /// <param name="input">The input data to transform</param>
    /// <returns>The transformed value</returns>
    Task<object?> ForwardAsync(object? input);

    /// <summary>
    /// Reverse the transformation (e.g., S3 URI back to file content).
    /// Called during job execution in Plasma.
    /// </summary>
    /// <param name="transformedInput">The transformed value to reverse</param>
    /// <param name="services">Service provider for accessing dependencies</param>
    /// <returns>The original data in a format suitable for HTTP requests</returns>
    Task<object?> ReverseAsync(object? transformedInput, IServiceProvider services);
}

/// <summary>
/// Interface for input transformations that can be applied during async job creation
/// and reversed during job execution.
/// </summary>
/// <typeparam name="TOptions">The options type for this transform</typeparam>
public interface ITransform<TOptions> : ITransform where TOptions : class
{
}

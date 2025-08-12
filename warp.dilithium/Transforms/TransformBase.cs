namespace Warp.Dilithium.Transforms;

/// <summary>
/// Base class for transforms that enforces the options constructor pattern
/// </summary>
/// <typeparam name="TOptions">The options type for this transform</typeparam>
public abstract class TransformBase<TOptions> : ITransform<TOptions> where TOptions : class
{
    protected TOptions Options { get; }

    protected TransformBase(TOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Transform input data (e.g., file content to blob URI).
    /// Called during async job creation after data has been extracted from HTTP context.
    /// </summary>
    /// <param name="input">The input data to transform</param>
    /// <returns>The transformed value</returns>
    public abstract Task<object?> ForwardAsync(object? input);

    /// <summary>
    /// Reverse the transformation (e.g., blob URI back to file content).
    /// Called during job execution in Plasma.
    /// </summary>
    /// <param name="transformedInput">The transformed value to reverse</param>
    /// <param name="services">Service provider for accessing dependencies</param>
    /// <returns>The original data in a format suitable for HTTP requests</returns>
    public abstract Task<object?> ReverseAsync(object? transformedInput, IServiceProvider services);
}

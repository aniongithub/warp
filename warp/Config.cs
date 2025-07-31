using Warp.Core.Helper;

namespace Warp;

internal class RouteDescriptor
{
    public string Path { get; set; } = "";
    public string Cluster { get; set; } = "";
    public List<string>? Preprocess { get; set; }
    public List<string>? Postprocess { get; set; }

    public bool TracingEnabled { get; set; } = false;
    public string? TracingProvider { get; set; }
    public string? TraceName { get; set; }

    private TracingProvider? _tracingProviderInstance;
    public TracingProvider? TracingProviderInstance => _tracingProviderInstance;
    private bool _initialized = false;
    public bool Initialized => _initialized;
    public void Initialize()
    {
        if (TracingEnabled && !string.IsNullOrWhiteSpace(TracingProvider))
        {
            var providerType = Type.GetType(TracingProvider);
            if (providerType != null)
            {
                _tracingProviderInstance = (TracingProvider?)Activator.CreateInstance(providerType, TraceName ?? $"{Cluster}.{Path}");
            }
        }
        _initialized = true;
    }
}
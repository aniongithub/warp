namespace Warp;

internal class RouteDescriptor
{
    public string Path { get; set; } = "";
    public string Cluster { get; set; } = "";
    public List<string>? Preprocess { get; set; }
    public List<string>? Postprocess { get; set; }
}
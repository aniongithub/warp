using System.Text.Json;

namespace Warp.Core.Job;

/// <summary>
/// Contains routing information extracted from the HTTP request for job processing
/// </summary>
public class JobRoutingInfo
{
    public string OriginalPath { get; set; } = string.Empty;
    public string ClusterId { get; set; } = string.Empty;
    public string TargetDestination { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
}

/// <summary>
/// Input data structure for jobs, containing parameters and routing information
/// </summary>
public class JobInput
{
    public string OriginalPath { get; set; } = string.Empty;
    public string ClusterId { get; set; } = string.Empty;
    public string TargetDestination { get; set; } = string.Empty;
    public Dictionary<string, object?> Parameters { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    /// Creates a JobInput from routing information and extracted parameters
    /// </summary>
    public static JobInput FromRoutingInfo(JobRoutingInfo routingInfo, Dictionary<string, object?> parameters)
    {
        return new JobInput
        {
            OriginalPath = routingInfo.OriginalPath,
            ClusterId = routingInfo.ClusterId,
            TargetDestination = routingInfo.TargetDestination,
            Parameters = parameters,
            Headers = routingInfo.Headers
        };
    }

    /// <summary>
    /// Deserializes job input from a JSON string
    /// </summary>
    public static JobInput? FromJson(string json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
            
        return JsonSerializer.Deserialize<JobInput>(json);
    }

    /// <summary>
    /// Serializes job input to a JSON string
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this);
    }
}

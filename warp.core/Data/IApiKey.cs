namespace Warp.Core.Data;
public interface IApiKey : IEntity
{
    string Key { get; set; }
    string Owner { get; set; }
    bool IsActive { get; set; }
    List<string> Permissions { get; set; } // replaces ProductTier
    float RateLimitHz { get; set; }
}
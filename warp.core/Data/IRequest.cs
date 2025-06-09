namespace Warp.Core.Data;

public interface IRequest : IEntity
{
    string Key { get; set; }
    DateTime LastUsed { get; set; }
    float LastRate { get; set; }
}

using System;

namespace Warp.Core.Data
{
    public interface IEvent : IEntity
    {
        string Key { get; set; }
        string EventType { get; set; }
        DateTime Timestamp { get; set; }
    }
}

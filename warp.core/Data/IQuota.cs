namespace Warp.Core.Data;

public interface IQuota : IEntity
{
    string Key { get; set; } // user or API key
    string QuotaName { get; set; }
    float Used { get; set; }
    float Limit { get; set; }
    string Type { get; set; } // "prepaid" or "postpaid"
}

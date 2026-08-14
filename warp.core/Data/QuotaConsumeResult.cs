namespace Warp.Core.Data;

/// <summary>
/// Outcome of an atomic quota consume operation (see <see cref="IDataContext.TryConsumeQuotaAsync"/>).
/// </summary>
public enum QuotaConsumeResult
{
    /// <summary>The usage was atomically recorded against the quota.</summary>
    Consumed,

    /// <summary>The quota is prepaid and consuming would exceed its limit; nothing was recorded.</summary>
    LimitExceeded,

    /// <summary>No quota with the supplied id exists.</summary>
    NotFound
}

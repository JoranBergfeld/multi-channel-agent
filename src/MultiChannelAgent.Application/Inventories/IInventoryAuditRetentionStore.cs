namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// The bounded delete behind the ninety-day audit retention the specification requires.
///
/// <c>AuditFact.RetentionDays</c> has said ninety since audits existed, but nothing enforced it: the
/// shipped cleanup covers confirmation proposals and outcome payloads only. #34 requires that "only
/// the specified 90-day semantic facts remain", so the sweep lives here and covers every audit fact,
/// not only the import one.
/// </summary>
public interface IInventoryAuditRetentionStore
{
    /// <summary>Deletes audit facts that occurred before <paramref name="cutoff"/>, at most <paramref name="maxRows"/> of them.</summary>
    Task<int> DeleteOccurredBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken);
}

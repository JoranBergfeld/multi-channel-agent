namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// One recorded effect of one applied change. Stock Entry identities are stored as plain values with
/// no foreign key on purpose: a merge or a Forget deletes the very row this records, and the record
/// of what happened must outlive it.
/// </summary>
public sealed class StockChangeSetEffectEntity
{
    public Guid Id { get; set; }

    public Guid OperationId { get; set; }

    /// <summary>1-based position within the change set, so the recorded effects read back in the order they were applied.</summary>
    public int Order { get; set; }

    public required string Kind { get; set; }

    public required string Effect { get; set; }

    public Guid SourceStockEntryId { get; set; }

    public required string SourceName { get; set; }

    public required string SourceUnitCanonicalName { get; set; }

    public string? SourceLocationName { get; set; }

    public decimal SourcePreviousQuantity { get; set; }

    public decimal SourceResultingQuantity { get; set; }

    public bool SourceRetired { get; set; }

    public Guid? DestinationStockEntryId { get; set; }

    public string? DestinationName { get; set; }

    public string? DestinationUnitCanonicalName { get; set; }

    public string? DestinationLocationName { get; set; }

    public decimal? DestinationPreviousQuantity { get; set; }

    public decimal? DestinationResultingQuantity { get; set; }

    /// <summary>How much this change actually moved from source to destination; zero when nothing moved.</summary>
    public decimal TransferredQuantity { get; set; }

    /// <summary>The exact new display name a Rename applied, or null for every other effect.</summary>
    public string? NewName { get; set; }
}

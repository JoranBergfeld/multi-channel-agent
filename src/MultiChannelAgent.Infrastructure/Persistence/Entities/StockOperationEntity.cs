namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable ledger row for one applied Inventory mutation, keyed by the operation identity derived
/// from the Turn and tool that produced it. Its whole purpose is retry safety: a replayed Turn finds
/// its own row here and re-reports exactly what it did, so the same Add can never be applied twice by
/// a redelivery, a restart, or a competing replica.
///
/// It stores the semantic facts an answer needs and nothing more - no prompts, no raw payloads, no
/// concurrency stamps, and no audit identity.
/// </summary>
public sealed class StockOperationEntity
{
    public Guid OperationId { get; set; }

    public Guid InventoryId { get; set; }

    /// <summary>The mutation kind as text ("Add", "Remove", "Set"), so the ledger reads as itself without a lookup.</summary>
    public required string Kind { get; set; }

    public Guid StockEntryId { get; set; }

    public required string Name { get; set; }

    public required string UnitCanonicalName { get; set; }

    public string? LocationName { get; set; }

    public string? Note { get; set; }

    public decimal PreviousQuantity { get; set; }

    public decimal ResultingQuantity { get; set; }

    public bool CreatedEntry { get; set; }

    public bool NotePreserved { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}

namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>The durable, exact Initial Import proposal awaiting one terminal settlement.</summary>
public sealed class ImportProposalEntity
{
    public Guid ProposalId { get; set; }

    public required string TokenHash { get; set; }

    public Guid ParticipantId { get; set; }

    public Guid InventoryId { get; set; }

    public required string FileDigest { get; set; }

    public required string Status { get; set; }

    public required string EntriesJson { get; set; }

    public int ExpectedStockEntryCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public long ExpiresAtTicks { get; set; }

    public DateTimeOffset? SettledAt { get; set; }

    public long? SettledAtTicks { get; set; }
}

namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>The durable semantic ledger header for one applied Initial Import.</summary>
public sealed class ImportOperationEntity
{
    public Guid OperationId { get; set; }

    public Guid InventoryId { get; set; }

    public Guid ProposalId { get; set; }

    public Guid ActorId { get; set; }

    public required string FileDigest { get; set; }

    public int CreatedEntryCount { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}

namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable ledger header for one applied change set. Its whole purpose is retry safety: a
/// re-driven Turn finds its own row here and re-reports exactly what it did, so one confirmed
/// proposal can never be applied twice by a redelivery, a restart, or a competing replica.
///
/// It carries semantic facts and nothing more - no prompts, no raw payloads, no concurrency stamps,
/// and no audit identity.
/// </summary>
public sealed class StockChangeSetOperationEntity
{
    public Guid OperationId { get; set; }

    public Guid InventoryId { get; set; }

    /// <summary>
    /// The Turn that caused this execution. Unique per Inventory, which is what makes the replay
    /// lookup deterministic without needing the proposal - by replay time it has been consumed.
    /// </summary>
    public Guid ConfirmedByTurnId { get; set; }

    /// <summary>The proposal this consumed, or null for an immediate change that needed none.</summary>
    public Guid? ProposalId { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}

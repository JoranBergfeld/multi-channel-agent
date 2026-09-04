namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable ledger header for one applied reference administration change set. Its whole purpose
/// is retry safety: a re-driven Turn finds its own row here and re-reports exactly what it did, so
/// one confirmed Retire can never be applied twice by a redelivery, a restart, or a competing
/// replica.
///
/// It is a separate table from the stock change-set ledger because the two record different work and
/// their identities are derived from different material; nothing about one can ever be mistaken for
/// the other.
/// </summary>
public sealed class ReferenceOperationEntity
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

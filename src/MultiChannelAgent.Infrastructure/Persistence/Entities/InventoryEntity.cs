namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row for one Inventory. <see cref="ClientRequestId"/> is the caller-supplied idempotency
/// key: the unique index on (CreatedByParticipantId, ClientRequestId) guarantees resubmitting the same
/// pair - including two concurrent deliveries of it - can never create a second Inventory row.
/// Duplicate <see cref="Name"/> values across different Owners are explicitly allowed; disambiguation
/// happens in the view via Owner display name and <see cref="Id"/>'s short form, never by rejecting
/// creation.
/// </summary>
public sealed class InventoryEntity
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string NormalizedName { get; set; }

    public Guid CreatedByParticipantId { get; set; }

    public required string ClientRequestId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

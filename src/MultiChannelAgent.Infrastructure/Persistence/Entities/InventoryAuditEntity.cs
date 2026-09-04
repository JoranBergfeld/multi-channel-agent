namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row for one semantic audit fact (see <see cref="Domain.Inventories.AuditFact"/>):
/// membership/ownership/recovery/denied-access outcomes, retained for 90 days from
/// <see cref="OccurredAtUtc"/>. Deliberately carries no stock details, prompt/content, secrets, SQL
/// diagnostics, or raw payloads; <see cref="Id"/> is an internal row identity never returned by any
/// API.
/// </summary>
public sealed class InventoryAuditEntity
{
    public Guid Id { get; set; }

    public required string EventType { get; set; }

    public required string ActorKind { get; set; }

    public required string ActorId { get; set; }

    public Guid InventoryId { get; set; }

    public Guid? SubjectParticipantId { get; set; }

    public required string OutcomeCode { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>
    /// <see cref="OccurredAtUtc"/> as UTC ticks. Retention sweeps compare and order on this rather
    /// than on the <see cref="DateTimeOffset"/> itself, because that comparison is not translatable
    /// on every relational provider the tests run against - and a retention rule that cannot be
    /// executed everywhere it is claimed is not a retention rule. Required so no writer can leave it
    /// at zero, which would make a fact recorded today look older than every other one forever.
    /// </summary>
    public required long OccurredAtUtcTicks { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }
}

using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>Maps a Domain <see cref="AuditFact"/> to its durable row, shared by every SQL governance store so the mapping happens in exactly one place.</summary>
internal static class InventoryAuditMapper
{
    public static InventoryAuditEntity ToEntity(AuditFact fact) => new()
    {
        Id = fact.Id,
        EventType = fact.EventType.ToString(),
        ActorKind = fact.ActorKind.ToString(),
        ActorId = fact.ActorId,
        InventoryId = fact.InventoryId.Value,
        SubjectParticipantId = fact.SubjectParticipantId?.Value,
        OutcomeCode = fact.OutcomeCode,
        OccurredAtUtc = fact.OccurredAt,
        ExpiresAtUtc = fact.ExpiresAt,
    };
}

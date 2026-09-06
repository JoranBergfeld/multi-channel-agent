namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// Persistence entity for a Voice session. Maps all domain fields exactly, including the strongly
/// typed ChannelConversationId.Value and immutable deadline timestamps. Timestamps that participate
/// in server-side WHERE comparisons are stored as UTC ticks (long) — the same pattern used throughout
/// this codebase — so the query translates identically on both SQL Server and SQLite.
/// </summary>
public sealed class VoiceSessionEntity
{
    public Guid Id { get; set; }

    public Guid ParticipantId { get; set; }

    public required string ChannelConversationId { get; set; }

    public string? ControlSessionId { get; set; }

    public required string OwnerInstanceId { get; set; }

    public required string Status { get; set; }

    public bool OccupiesSlot { get; set; }

    public long StartedAtTicks { get; set; }

    public long LastHeartbeatAtTicks { get; set; }

    public long? EndedAtTicks { get; set; }

    public long ExpiresAtTicks { get; set; }

    public long WarningAtTicks { get; set; }

    public long IdleExpiresAtTicks { get; set; }

    public bool WarningIssued { get; set; }
}

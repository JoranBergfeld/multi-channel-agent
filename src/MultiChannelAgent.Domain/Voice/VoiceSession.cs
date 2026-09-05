using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Voice;

/// <summary>
/// Represents the bounded lifecycle of a single Voice Live session from reservation through termination.
/// </summary>
public sealed class VoiceSession
{
    public VoiceSessionId Id { get; }
    public ParticipantId ParticipantId { get; }
    public string ChannelConversationId { get; }
    public string? ControlSessionId { get; private set; }
    public string OwnerInstanceId { get; }
    public VoiceSessionStatus Status { get; private set; }
    public bool OccupiesSlot { get; private set; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset LastHeartbeatAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; }
    public DateTimeOffset WarningAt { get; }
    public DateTimeOffset IdleExpiresAt { get; private set; }
    public bool WarningIssued { get; private set; }

    private VoiceSession(
        VoiceSessionId id,
        ParticipantId participantId,
        string channelConversationId,
        string? controlSessionId,
        string ownerInstanceId,
        VoiceSessionStatus status,
        bool occupiesSlot,
        DateTimeOffset startedAt,
        DateTimeOffset lastHeartbeatAt,
        DateTimeOffset? endedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset warningAt,
        DateTimeOffset idleExpiresAt,
        bool warningIssued)
    {
        Id = id;
        ParticipantId = participantId;
        ChannelConversationId = channelConversationId;
        ControlSessionId = controlSessionId;
        OwnerInstanceId = ownerInstanceId;
        Status = status;
        OccupiesSlot = occupiesSlot;
        StartedAt = startedAt;
        LastHeartbeatAt = lastHeartbeatAt;
        EndedAt = endedAt;
        ExpiresAt = expiresAt;
        WarningAt = warningAt;
        IdleExpiresAt = idleExpiresAt;
        WarningIssued = warningIssued;
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Admits a new Voice session into the <see cref="VoiceSessionStatus.Negotiating"/> phase.
    /// Occupies a slot and records <paramref name="now"/> as both <see cref="StartedAt"/> and <see cref="LastHeartbeatAt"/>.
    /// </summary>
    public static VoiceSession Reserve(
        ParticipantId participantId,
        string channelConversationId,
        string ownerInstanceId,
        DateTimeOffset now,
        VoiceSessionDeadlines deadlines)
    {
        if (string.IsNullOrWhiteSpace(channelConversationId))
            throw new ArgumentException("Channel conversation ID must not be blank.", nameof(channelConversationId));
        if (string.IsNullOrWhiteSpace(ownerInstanceId))
            throw new ArgumentException("Owner instance ID must not be blank.", nameof(ownerInstanceId));
        if (deadlines.ExpiresAt <= now)
            throw new ArgumentException("ExpiresAt must be in the future.", nameof(deadlines));
        if (deadlines.WarningAt >= deadlines.ExpiresAt)
            throw new ArgumentException("WarningAt must be strictly before ExpiresAt.", nameof(deadlines));
        if (deadlines.IdleExpiresAt > deadlines.ExpiresAt)
            throw new ArgumentException("IdleExpiresAt must not exceed ExpiresAt.", nameof(deadlines));

        return new VoiceSession(
            id: new VoiceSessionId(Guid.NewGuid()),
            participantId: participantId,
            channelConversationId: channelConversationId,
            controlSessionId: null,
            ownerInstanceId: ownerInstanceId,
            status: VoiceSessionStatus.Negotiating,
            occupiesSlot: true,
            startedAt: now,
            lastHeartbeatAt: now,
            endedAt: null,
            expiresAt: deadlines.ExpiresAt,
            warningAt: deadlines.WarningAt,
            idleExpiresAt: deadlines.IdleExpiresAt,
            warningIssued: false);
    }

    // ── Transitions ───────────────────────────────────────────────────────────

    /// <summary>
    /// Transitions from <see cref="VoiceSessionStatus.Negotiating"/> to <see cref="VoiceSessionStatus.Active"/>
    /// once the control channel has been established.
    /// </summary>
    public void Activate(string controlSessionId, DateTimeOffset now)
    {
        if (Status != VoiceSessionStatus.Negotiating)
            throw new InvalidOperationException($"Cannot activate a session in state {Status}.");
        if (string.IsNullOrWhiteSpace(controlSessionId))
            throw new ArgumentException("Control session ID must not be blank.", nameof(controlSessionId));
        if (now < StartedAt)
            throw new ArgumentException("Activation time must not be before StartedAt.", nameof(now));

        ControlSessionId = controlSessionId;
        Status = VoiceSessionStatus.Active;
        LastHeartbeatAt = now;
    }

    /// <summary>
    /// Abandons the session while still in <see cref="VoiceSessionStatus.Negotiating"/>.
    /// Releases the slot.
    /// </summary>
    public void Abandon(DateTimeOffset now)
    {
        if (Status != VoiceSessionStatus.Negotiating)
            throw new InvalidOperationException($"Cannot abandon a session in state {Status}.");

        Status = VoiceSessionStatus.Ended;
        OccupiesSlot = false;
        EndedAt = now;
    }

    /// <summary>
    /// Ends the session from any state. Idempotent — once <see cref="EndedAt"/> is set it is never changed.
    /// </summary>
    public void End(DateTimeOffset now)
    {
        if (Status == VoiceSessionStatus.Ended)
            return;

        Status = VoiceSessionStatus.Ended;
        OccupiesSlot = false;
        EndedAt = now;
    }

    /// <summary>
    /// Records a heartbeat for an <see cref="VoiceSessionStatus.Active"/> session.
    /// Updates <see cref="LastHeartbeatAt"/> and extends <see cref="IdleExpiresAt"/> by
    /// <paramref name="idleTimeout"/>, clamped to <see cref="ExpiresAt"/>.
    /// </summary>
    public void RecordHeartbeat(DateTimeOffset now, TimeSpan idleTimeout)
    {
        if (Status != VoiceSessionStatus.Active)
            throw new InvalidOperationException($"Cannot record a heartbeat for a session in state {Status}.");
        if (idleTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Idle timeout must be positive.", nameof(idleTimeout));
        if (now < LastHeartbeatAt)
            throw new ArgumentException("Heartbeat time must not go backward.", nameof(now));

        LastHeartbeatAt = now;
        var candidate = now + idleTimeout;
        IdleExpiresAt = candidate <= ExpiresAt ? candidate : ExpiresAt;
    }

    /// <summary>
    /// Returns <see langword="true"/> exactly once when <paramref name="now"/> is at or after
    /// <see cref="WarningAt"/> for an <see cref="VoiceSessionStatus.Active"/> non-expired session,
    /// then sets <see cref="WarningIssued"/> so subsequent calls return <see langword="false"/>.
    /// </summary>
    public bool ShouldWarn(DateTimeOffset now)
    {
        if (Status != VoiceSessionStatus.Active || IsExpired(now) || WarningIssued)
            return false;
        if (now >= WarningAt)
        {
            WarningIssued = true;
            return true;
        }
        return false;
    }

    // ── Computed state ────────────────────────────────────────────────────────

    /// <summary>Returns <see langword="true"/> when <paramref name="now"/> is at or past <see cref="ExpiresAt"/>.</summary>
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>
    /// Returns <see langword="true"/> when the session is <see cref="VoiceSessionStatus.Active"/> and
    /// <paramref name="now"/> is at or past <see cref="IdleExpiresAt"/>.
    /// </summary>
    public bool IsIdle(DateTimeOffset now) => Status == VoiceSessionStatus.Active && now >= IdleExpiresAt;

    // ── Reconstitution ────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds a <see cref="VoiceSession"/> from persisted state with invariant validation.
    /// For use by the persistence layer (Task 4) only.
    /// </summary>
    public static VoiceSession Reconstitute(
        VoiceSessionId id,
        ParticipantId participantId,
        string channelConversationId,
        string? controlSessionId,
        string ownerInstanceId,
        VoiceSessionStatus status,
        bool occupiesSlot,
        DateTimeOffset startedAt,
        DateTimeOffset lastHeartbeatAt,
        DateTimeOffset? endedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset warningAt,
        DateTimeOffset idleExpiresAt,
        bool warningIssued)
    {
        if (id.Value == Guid.Empty)
            throw new ArgumentException("Session ID must not be empty.", nameof(id));
        if (string.IsNullOrWhiteSpace(channelConversationId))
            throw new ArgumentException("Channel conversation ID must not be blank.", nameof(channelConversationId));
        if (string.IsNullOrWhiteSpace(ownerInstanceId))
            throw new ArgumentException("Owner instance ID must not be blank.", nameof(ownerInstanceId));
        if (status == VoiceSessionStatus.Active && string.IsNullOrWhiteSpace(controlSessionId))
            throw new ArgumentException("Active session must have a control session ID.", nameof(controlSessionId));
        if (status == VoiceSessionStatus.Ended && endedAt is null)
            throw new ArgumentException("Ended session must have an EndedAt timestamp.", nameof(endedAt));
        if (lastHeartbeatAt < startedAt)
            throw new ArgumentException("LastHeartbeatAt must not be before StartedAt.", nameof(lastHeartbeatAt));

        return new VoiceSession(
            id: id,
            participantId: participantId,
            channelConversationId: channelConversationId,
            controlSessionId: controlSessionId,
            ownerInstanceId: ownerInstanceId,
            status: status,
            occupiesSlot: occupiesSlot,
            startedAt: startedAt,
            lastHeartbeatAt: lastHeartbeatAt,
            endedAt: endedAt,
            expiresAt: expiresAt,
            warningAt: warningAt,
            idleExpiresAt: idleExpiresAt,
            warningIssued: warningIssued);
    }
}

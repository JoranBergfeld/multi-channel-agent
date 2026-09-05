using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

/// <summary>
/// Thread-safe in-memory <see cref="IVoiceSessionStore"/> for unit tests. Uses a single lock to
/// mirror the SQL store's serializable-transaction semantics, and clones/reconstitutes sessions
/// so tests never pass due to mutable shared-reference aliasing.
/// </summary>
public sealed class InMemoryVoiceSessionStore : IVoiceSessionStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, VoiceSession> _sessions = new();

    public Task<VoiceAdmissionResult> TryAdmitAsync(VoiceSession session, int globalCap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            var hasExisting = _sessions.Values.Any(s =>
                s.ParticipantId == session.ParticipantId && s.OccupiesSlot);

            if (hasExisting)
                return Task.FromResult(VoiceAdmissionResult.Denied(VoiceAdmissionDenialReason.AlreadyActive));

            var occupyingCount = _sessions.Values.Count(s => s.OccupiesSlot);
            if (occupyingCount >= globalCap)
                return Task.FromResult(VoiceAdmissionResult.Denied(VoiceAdmissionDenialReason.GlobalCapReached));

            _sessions[session.Id.Value] = Clone(session);
            return Task.FromResult(VoiceAdmissionResult.Success(Clone(session)));
        }
    }

    public Task<VoiceSession?> FindByIdAsync(VoiceSessionId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            return Task.FromResult(_sessions.TryGetValue(id.Value, out var s) ? Clone(s) : null);
        }
    }

    public Task<bool> UpdateAsync(VoiceSession session, VoiceSessionStatus expectedStatus, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_sessions.TryGetValue(session.Id.Value, out var existing))
                return Task.FromResult(false);

            if (existing.Status != expectedStatus)
                return Task.FromResult(false);

            _sessions[session.Id.Value] = Clone(session);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<VoiceSession>> FindExpiredOrIdleAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            var result = _sessions.Values
                .Where(s => s.Status != VoiceSessionStatus.Ended)
                .Where(s => s.IsExpired(now) || s.IsIdle(now))
                .Select(Clone)
                .ToList();

            return Task.FromResult<IReadOnlyList<VoiceSession>>(result);
        }
    }

    public Task<IReadOnlyList<VoiceSession>> FindByOwnerInstanceAsync(string ownerInstanceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            var result = _sessions.Values
                .Where(s => s.Status != VoiceSessionStatus.Ended && s.OwnerInstanceId == ownerInstanceId)
                .Select(Clone)
                .ToList();

            return Task.FromResult<IReadOnlyList<VoiceSession>>(result);
        }
    }

    private static VoiceSession Clone(VoiceSession s) =>
        VoiceSession.Reconstitute(
            s.Id,
            s.ParticipantId,
            s.ChannelConversationId,
            s.ControlSessionId,
            s.OwnerInstanceId,
            s.Status,
            s.OccupiesSlot,
            s.StartedAt,
            s.LastHeartbeatAt,
            s.EndedAt,
            s.ExpiresAt,
            s.WarningAt,
            s.IdleExpiresAt,
            s.WarningIssued);
}

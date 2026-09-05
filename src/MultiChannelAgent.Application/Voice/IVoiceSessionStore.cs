using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Voice;

/// <summary>
/// Durable store for <see cref="VoiceSession"/> lifecycle persistence. Admission atomically enforces
/// the per-participant uniqueness constraint and the global concurrent-session cap.
/// </summary>
public interface IVoiceSessionStore
{
    /// <summary>
    /// Atomically admits <paramref name="session"/> if the participant has no slot-occupying session
    /// and the global cap has not been reached. Returns a typed result distinguishing
    /// <see cref="VoiceAdmissionDenialReason.AlreadyActive"/> from
    /// <see cref="VoiceAdmissionDenialReason.GlobalCapReached"/>.
    /// </summary>
    Task<VoiceAdmissionResult> TryAdmitAsync(VoiceSession session, int globalCap, CancellationToken cancellationToken);

    /// <summary>Returns the session with the given ID, or <see langword="null"/> if not found.</summary>
    Task<VoiceSession?> FindByIdAsync(VoiceSessionId id, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the current state of <paramref name="session"/>. Uses an optimistic guard on
    /// (Id, Status) where lifecycle transitions matter — the update is a no-op (returns
    /// <see langword="false"/>) if the row's status no longer matches the expected
    /// <paramref name="expectedStatus"/>.
    /// </summary>
    Task<bool> UpdateAsync(VoiceSession session, VoiceSessionStatus expectedStatus, CancellationToken cancellationToken);

    /// <summary>
    /// Returns all sessions that are expired (now ≥ ExpiresAt) or idle (Active and now ≥ IdleExpiresAt)
    /// and have not yet ended.
    /// </summary>
    Task<IReadOnlyList<VoiceSession>> FindExpiredOrIdleAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns all non-ended sessions owned by <paramref name="ownerInstanceId"/>.</summary>
    Task<IReadOnlyList<VoiceSession>> FindByOwnerInstanceAsync(string ownerInstanceId, CancellationToken cancellationToken);
}

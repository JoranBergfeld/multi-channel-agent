using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Voice;

/// <summary>
/// Manages voice session heartbeats and participant-initiated releases. Heartbeat returns
/// authoritative lifecycle state with priority: expired → idle → warning_due → active.
/// Release ends a session and terminates the gateway handle when present. Both validate
/// participant ownership.
/// </summary>
public sealed class VoiceSessionReleaseService(
    IVoiceSessionStore store,
    IVoiceLiveGateway gateway,
    TimeProvider timeProvider,
    TimeSpan idleTimeout)
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private const int MaxCasRetries = 3;

    private static readonly HeartbeatResult NotFound =
        new(Renewed: false, LifecycleState: "not_found", RemainingSeconds: null, ForcedCloseReason: null);

    /// <summary>
    /// Records a heartbeat for the given session if it belongs to <paramref name="participantId"/>.
    /// Returns authoritative lifecycle state with priority: expired → idle → warning_due → active.
    /// Expired and idle sessions are not renewed. Missing or wrong-participant sessions return
    /// <c>not_found</c> indistinguishably.
    /// </summary>
    public async Task<HeartbeatResult> HeartbeatAsync(
        VoiceSessionId sessionId, ParticipantId participantId, CancellationToken ct)
    {
        var session = await store.FindByIdAsync(sessionId, ct);
        if (session is null || session.ParticipantId != participantId)
            return NotFound;

        // A trailing heartbeat after release finds the session Ended. Task 6 lifecycle
        // vocabulary has no "ended" state — treat it as unavailable, identical to not_found.
        if (session.Status == VoiceSessionStatus.Ended)
            return NotFound;

        var now = timeProvider.GetUtcNow();

        if (session.IsExpired(now))
            return new HeartbeatResult(Renewed: false, LifecycleState: "expired",
                RemainingSeconds: null, ForcedCloseReason: "expired");

        if (session.IsIdle(now))
            return new HeartbeatResult(Renewed: false, LifecycleState: "idle",
                RemainingSeconds: null, ForcedCloseReason: "idle");

        var lifecycleState = session.ShouldWarn(now) ? "warning_due" : "active";

        session.RecordHeartbeat(now, idleTimeout);
        var updated = await store.UpdateAsync(session, VoiceSessionStatus.Active, ct);
        if (!updated)
            throw new InvalidOperationException(
                "Voice session heartbeat conflict: the session was modified concurrently.");

        var remainingSeconds = (int)(session.ExpiresAt - now).TotalSeconds;
        return new HeartbeatResult(Renewed: true, LifecycleState: lifecycleState,
            RemainingSeconds: remainingSeconds, ForcedCloseReason: null);
    }

    /// <summary>
    /// Releases the session if it belongs to <paramref name="participantId"/>. Idempotent for
    /// already-ended or non-existent sessions. Terminates the gateway handle when
    /// <see cref="VoiceSession.ControlSessionId"/> is present. Uses a bounded non-cancelled
    /// token for mandatory cleanup work. On CAS conflict, re-reads and retries so that a
    /// concurrent Negotiating→Active transition still terminates the actual provider session.
    /// </summary>
    public async Task ReleaseAsync(
        VoiceSessionId sessionId, ParticipantId participantId, CancellationToken ct)
    {
        var session = await store.FindByIdAsync(sessionId, ct);
        if (session is null || session.ParticipantId != participantId)
            return;

        if (session.Status == VoiceSessionStatus.Ended)
            return;

        using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
        var failures = new List<Exception>();
        var terminatedControlIds = new HashSet<string>();

        for (int attempt = 0; attempt < MaxCasRetries; attempt++)
        {
            var expectedStatus = session.Status;
            var controlSessionId = session.ControlSessionId;
            session.End(timeProvider.GetUtcNow());

            if (controlSessionId is not null && terminatedControlIds.Add(controlSessionId))
            {
                try
                {
                    await gateway.TerminateAsync(controlSessionId, cleanupCts.Token);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            bool persisted;
            try
            {
                persisted = await store.UpdateAsync(session, expectedStatus, cleanupCts.Token);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                break;
            }

            if (persisted)
            {
                if (failures.Count > 0)
                    throw new AggregateException(failures);
                return;
            }

            // CAS conflict — re-read to observe latest state
            VoiceSession? reread;
            try
            {
                reread = await store.FindByIdAsync(sessionId, cleanupCts.Token);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                break;
            }

            if (reread is null || reread.ParticipantId != participantId)
                return;

            if (reread.Status == VoiceSessionStatus.Ended)
            {
                if (failures.Count > 0)
                    throw new AggregateException(failures);
                return;
            }

            session = reread;
        }

        failures.Add(new InvalidOperationException(
            $"Voice session release could not be persisted after {MaxCasRetries} attempts."));
        throw new AggregateException(failures);
    }
}

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
    /// token for mandatory cleanup work.
    /// </summary>
    public async Task ReleaseAsync(
        VoiceSessionId sessionId, ParticipantId participantId, CancellationToken ct)
    {
        var session = await store.FindByIdAsync(sessionId, ct);
        if (session is null || session.ParticipantId != participantId)
            return;

        if (session.Status == VoiceSessionStatus.Ended)
            return;

        var previousStatus = session.Status;
        var controlSessionId = session.ControlSessionId;
        session.End(timeProvider.GetUtcNow());

        using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
        var failures = new List<Exception>();

        if (controlSessionId is not null)
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

        try
        {
            await store.UpdateAsync(session, previousStatus, cleanupCts.Token);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        if (failures.Count > 0)
            throw new AggregateException(failures);
    }
}

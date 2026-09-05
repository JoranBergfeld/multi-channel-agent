using System.Runtime.ExceptionServices;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Voice;

/// <summary>
/// Orchestrates the voice session admission lifecycle: reserve a slot atomically, negotiate the
/// WebRTC session with the Voice Live gateway outside any SQL transaction, and either activate
/// the session on success or abandon the reservation on failure. Failures always reclaim
/// capacity; cleanup uses a bounded non-cancelled token even when the caller has cancelled.
/// </summary>
public sealed class VoiceAdmissionService(
    IVoiceSessionStore store,
    IVoiceLiveGateway gateway,
    VoiceOptions options,
    TimeProvider timeProvider,
    string ownerInstanceId)
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Attempts to admit a voice session for the given participant and conversation.
    /// Returns a typed result distinguishing success (with opaque session ID and SDP answer)
    /// from denial (with a specific reason). On gateway or activation failure the reservation
    /// is abandoned and capacity reclaimed before the exception propagates.
    /// </summary>
    public async Task<VoiceConnectionAdmissionResult> AdmitAsync(
        ParticipantId participantId,
        ChannelConversationId channelConversationId,
        string sdpOffer,
        CancellationToken ct)
    {
        // 1. Disabled — fast exit without touching store or gateway.
        if (!options.Enabled)
            return VoiceConnectionAdmissionResult.Denied(VoiceAdmissionDenialReason.VoiceDisabled);

        // 2. Snapshot time and reserve.
        var now = timeProvider.GetUtcNow();
        var session = VoiceSession.Reserve(
            participantId, channelConversationId, ownerInstanceId, now, options.ComputeDeadlines(now));

        // 3. Atomic admission — preserve AlreadyActive vs GlobalCapReached.
        var admission = await store.TryAdmitAsync(session, options.GlobalActiveCap, ct);
        if (!admission.Admitted)
            return VoiceConnectionAdmissionResult.Denied(admission.DenialReason!.Value);

        session = admission.Session!;

        // 4. Negotiate outside any SQL transaction.
        VoiceLiveNegotiationResult negotiation;
        try
        {
            negotiation = await gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(sdpOffer), ct);
        }
        catch (Exception ex)
        {
            // 5. Negotiation failed — abandon reservation, reclaim capacity, rethrow.
            await AbandonReservationAsync(session, ex);
            throw; // Unreachable — AbandonReservationAsync always throws.
        }

        // 6. Activate with ControlSessionId and persist.
        session.Activate(negotiation.ControlSessionId, timeProvider.GetUtcNow());

        bool activated;
        try
        {
            activated = await store.UpdateAsync(session, VoiceSessionStatus.Negotiating, ct);
        }
        catch (Exception ex)
        {
            // 7. Activation persist threw — terminate gateway, release reservation.
            await CleanupAfterActivationFailureAsync(session, negotiation.ControlSessionId, ex);
            throw; // Unreachable — cleanup always throws.
        }

        if (!activated)
        {
            // 7. Activation conflict (false return) — same cleanup path.
            var conflict = new InvalidOperationException(
                "Voice session activation conflict: the reservation was modified concurrently.");
            await CleanupAfterActivationFailureAsync(session, negotiation.ControlSessionId, conflict);
            throw conflict; // Unreachable — cleanup always throws.
        }

        // 8. Success — return opaque session ID + SDP answer, never ControlSessionId.
        return VoiceConnectionAdmissionResult.Success(session.Id, negotiation.SdpAnswer);
    }

    /// <summary>
    /// Abandons a <see cref="VoiceSessionStatus.Negotiating"/> reservation and rethrows the
    /// primary exception. Uses a bounded non-cancelled token so cleanup proceeds even when the
    /// caller's token is cancelled. Surfaces both failures as <see cref="AggregateException"/>
    /// when cleanup itself fails.
    /// </summary>
    private async Task AbandonReservationAsync(VoiceSession session, Exception primaryException)
    {
        using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
        try
        {
            session.Abandon(timeProvider.GetUtcNow());
            await store.UpdateAsync(session, VoiceSessionStatus.Negotiating, cleanupCts.Token);
        }
        catch (Exception cleanupEx)
        {
            throw new AggregateException(primaryException, cleanupEx);
        }

        ExceptionDispatchInfo.Capture(primaryException).Throw();
    }

    /// <summary>
    /// Terminates the gateway control session and releases the durable reservation after an
    /// activation failure. Uses a bounded non-cancelled token. Surfaces cleanup failures as
    /// <see cref="AggregateException"/> alongside the primary exception.
    /// </summary>
    private async Task CleanupAfterActivationFailureAsync(
        VoiceSession session, string controlSessionId, Exception primaryException)
    {
        using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
        var cleanupFailures = new List<Exception>();

        try
        {
            await gateway.TerminateAsync(controlSessionId, cleanupCts.Token);
        }
        catch (Exception ex)
        {
            cleanupFailures.Add(ex);
        }

        try
        {
            session.End(timeProvider.GetUtcNow());
            var released = await store.UpdateAsync(
                session, VoiceSessionStatus.Negotiating, cleanupCts.Token);
            if (!released)
            {
                // DB status changed concurrently — try Active as fallback.
                await store.UpdateAsync(session, VoiceSessionStatus.Active, cleanupCts.Token);
            }
        }
        catch (Exception ex)
        {
            cleanupFailures.Add(ex);
        }

        if (cleanupFailures.Count > 0)
            throw new AggregateException(
                new[] { primaryException }.Concat(cleanupFailures));

        ExceptionDispatchInfo.Capture(primaryException).Throw();
    }
}

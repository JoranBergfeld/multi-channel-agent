namespace MultiChannelAgent.Application.Voice;

/// <summary>Describes the SDP offer for a Voice Live WebRTC session negotiation.</summary>
/// <param name="SdpOffer">The SDP offer from the browser WebRTC peer connection.</param>
public sealed record VoiceLiveNegotiationRequest(string SdpOffer);

/// <summary>The result of a successful Voice Live session negotiation.</summary>
/// <param name="ControlSessionId">
/// An opaque identifier for the negotiated control session, used to terminate the session
/// or check ownership. Never forwarded to untrusted clients.
/// </param>
/// <param name="SdpAnswer">The SDP answer to relay back to the browser peer connection.</param>
public sealed record VoiceLiveNegotiationResult(string ControlSessionId, string SdpAnswer);

/// <summary>
/// Transport gateway for Voice Live WebRTC session lifecycle. Responsible for the SDP
/// offer/answer exchange, session termination, and server-side ownership checks.
/// Audio protocol and event routing are not in scope.
/// </summary>
public interface IVoiceLiveGateway
{
    /// <summary>Negotiates a new Voice Live WebRTC session and returns the answer and control identifier.</summary>
    Task<VoiceLiveNegotiationResult> NegotiateAsync(VoiceLiveNegotiationRequest request, CancellationToken cancellationToken);

    /// <summary>Terminates the specified session on the Voice Live service.</summary>
    Task TerminateAsync(string controlSessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns <see langword="true"/> if the specified control session is present and has
    /// not been terminated.
    /// </summary>
    bool OwnsSession(string controlSessionId);
}

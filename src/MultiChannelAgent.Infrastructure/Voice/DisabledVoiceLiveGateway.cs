using MultiChannelAgent.Application.Voice;

namespace MultiChannelAgent.Infrastructure.Voice;

/// <summary>
/// A no-op gateway registered when Voice is disabled. The <see cref="VoiceAdmissionService"/>
/// returns a <see cref="VoiceAdmissionDenialReason.VoiceDisabled"/> denial before ever calling
/// the gateway, so these methods should never be reached. If they are, the exception surfaces
/// the misconfiguration immediately rather than silently succeeding.
/// </summary>
public sealed class DisabledVoiceLiveGateway : IVoiceLiveGateway
{
    public Task<VoiceLiveNegotiationResult> NegotiateAsync(
        VoiceLiveNegotiationRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Voice Live gateway is disabled.");

    public Task TerminateAsync(string controlSessionId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public bool OwnsSession(string controlSessionId) => false;
}

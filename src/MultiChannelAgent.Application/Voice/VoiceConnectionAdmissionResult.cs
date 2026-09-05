using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Voice;

/// <summary>
/// Application-facing outcome of <see cref="VoiceAdmissionService.AdmitAsync"/>: either the session
/// was admitted (with an opaque <see cref="VoiceSessionId"/> and SDP answer) or admission was denied
/// for a typed reason. The transport-internal <c>ControlSessionId</c> is never exposed.
/// </summary>
public sealed record VoiceConnectionAdmissionResult
{
    public bool Admitted { get; }
    public VoiceSessionId? VoiceSessionId { get; }
    public string? SdpAnswer { get; }
    public VoiceAdmissionDenialReason? DenialReason { get; }

    private VoiceConnectionAdmissionResult(
        bool admitted, VoiceSessionId? voiceSessionId, string? sdpAnswer, VoiceAdmissionDenialReason? denialReason)
    {
        Admitted = admitted;
        VoiceSessionId = voiceSessionId;
        SdpAnswer = sdpAnswer;
        DenialReason = denialReason;
    }

    public static VoiceConnectionAdmissionResult Success(VoiceSessionId voiceSessionId, string sdpAnswer) =>
        new(true, voiceSessionId, sdpAnswer, null);

    public static VoiceConnectionAdmissionResult Denied(VoiceAdmissionDenialReason reason) =>
        new(false, null, null, reason);
}

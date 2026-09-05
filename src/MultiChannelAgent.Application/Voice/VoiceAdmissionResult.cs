using MultiChannelAgent.Domain.Voice;

namespace MultiChannelAgent.Application.Voice;

/// <summary>
/// Distinguishes why a voice session admission was denied so callers can report the correct reason
/// to the participant without inspecting opaque booleans.
/// </summary>
public enum VoiceAdmissionDenialReason
{
    /// <summary>The participant already has a Negotiating or Active session occupying a slot.</summary>
    AlreadyActive,

    /// <summary>The global concurrent-session cap has been reached.</summary>
    GlobalCapReached,
}

/// <summary>
/// The outcome of <see cref="IVoiceSessionStore.TryAdmitAsync"/>: either the session was admitted
/// (and its persisted state is returned) or admission was denied for a typed reason.
/// </summary>
public sealed record VoiceAdmissionResult
{
    public bool Admitted { get; }
    public VoiceSession? Session { get; }
    public VoiceAdmissionDenialReason? DenialReason { get; }

    private VoiceAdmissionResult(bool admitted, VoiceSession? session, VoiceAdmissionDenialReason? denialReason)
    {
        Admitted = admitted;
        Session = session;
        DenialReason = denialReason;
    }

    public static VoiceAdmissionResult Success(VoiceSession session) => new(true, session, null);
    public static VoiceAdmissionResult Denied(VoiceAdmissionDenialReason reason) => new(false, null, reason);
}
